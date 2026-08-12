import * as path from "node:path";
import * as fs from "node:fs";
import { partial_ratio } from "fuzzball";

/**
 * Port of CaravanCMS.Api/Services/FuzzyMatcher.cs — used only by the
 * one-time local→R2 file migration script (scripts/migrate-files.ts), never
 * by the live Worker (Workers has no local filesystem to scan at runtime).
 * Tiered confidence strategy, thresholds and formula kept identical to the
 * C# original so migrated links match what the old system would have chosen:
 *  1. VIN in filename (0.95)
 *  2. Registration in filename (0.88)
 *  3. VIN or reg in folder path (0.72)
 *  3.5. Same vehicle folder as an already-linked document (0.68 / 0.62)
 *  4. Fuzzy make/model match (0.35–0.60), fuzzball.partial_ratio ports
 *     FuzzySharp's Fuzz.PartialRatio (both descend from fuzzywuzzy).
 */

const MIN_FUZZY_SCORE = 65;

export interface CaravanForMatching {
  registrationNumber: string;
  vin: string | null;
  make: string | null;
  model: string | null;
  year: number | null;
}

export interface FileMatch {
  filePath: string;
  fileName: string;
  fileExtension: string;
  fileSizeBytes: number;
  confidence: number;
  matchMethod: string;
  suggestedRegistrationNumber: string | null;
  suggestedCaravanDescription: string | null;
  alreadyLinked: boolean;
}

function describeCaravan(c: CaravanForMatching): string {
  return `${c.year ?? ""} ${c.make ?? ""} ${c.model ?? ""} — ${c.registrationNumber || c.vin || "No ID"}`.trim();
}

function tryMatch(
  caravan: CaravanForMatching,
  fileName: string,
  folderPath: string,
  best: { confidence: number; suggestedRegistrationNumber: string | null; suggestedCaravanDescription: string | null; matchMethod: string },
): void {
  // Tier 1: VIN in filename
  if (caravan.vin && caravan.vin.length >= 5) {
    const vin = caravan.vin.toUpperCase();
    if (fileName.includes(vin)) {
      if (0.95 > best.confidence) {
        best.suggestedRegistrationNumber = caravan.registrationNumber;
        best.suggestedCaravanDescription = describeCaravan(caravan);
        best.confidence = 0.95;
        best.matchMethod = "VinInFilename";
      }
      return;
    }
  }

  // Tier 2: Registration in filename
  if (caravan.registrationNumber && caravan.registrationNumber.length >= 3) {
    const reg = caravan.registrationNumber.toUpperCase().replaceAll(" ", "");
    const fileNameClean = fileName.replaceAll(" ", "").replaceAll("-", "").replaceAll("_", "");
    if (fileNameClean.includes(reg)) {
      if (0.88 > best.confidence) {
        best.suggestedRegistrationNumber = caravan.registrationNumber;
        best.suggestedCaravanDescription = describeCaravan(caravan);
        best.confidence = 0.88;
        best.matchMethod = "RegInFilename";
      }
      return;
    }
  }

  // Tier 3: VIN or reg in folder path
  if (caravan.vin && caravan.vin.length >= 5 && folderPath.includes(caravan.vin.toUpperCase())) {
    if (0.72 > best.confidence) {
      best.suggestedRegistrationNumber = caravan.registrationNumber;
      best.suggestedCaravanDescription = describeCaravan(caravan);
      best.confidence = 0.72;
      best.matchMethod = "VinInFolderPath";
    }
    return;
  }
  if (
    caravan.registrationNumber &&
    caravan.registrationNumber.length >= 3 &&
    folderPath.includes(caravan.registrationNumber.toUpperCase().replaceAll(" ", ""))
  ) {
    if (0.72 > best.confidence) {
      best.suggestedRegistrationNumber = caravan.registrationNumber;
      best.suggestedCaravanDescription = describeCaravan(caravan);
      best.confidence = 0.72;
      best.matchMethod = "RegInFolderPath";
    }
    return;
  }

  // Tier 4: Fuzzy make/model
  if (caravan.make && caravan.model) {
    const target = `${caravan.make} ${caravan.model}`.toUpperCase();
    const score = partial_ratio(fileName, target);
    if (score >= MIN_FUZZY_SCORE) {
      const confidence = 0.35 + ((score - MIN_FUZZY_SCORE) / (100 - MIN_FUZZY_SCORE)) * 0.25;
      if (confidence > best.confidence) {
        best.suggestedRegistrationNumber = caravan.registrationNumber;
        best.suggestedCaravanDescription = describeCaravan(caravan);
        best.confidence = confidence;
        best.matchMethod = "FuzzyMakeModel";
      }
    }
  }
}

/** Directory -> single RegistrationNumber, built from already-linked document paths (Tier 3.5). */
export function buildFolderRegoIndex(linkedDocs: Array<{ filePath: string; rego: string }>): Map<string, string> {
  const folderRegos = new Map<string, Set<string>>();

  for (const { filePath, rego } of linkedDocs) {
    let dir: string | null = path.dirname(filePath);
    for (let depth = 0; depth < 6 && dir !== null && dir !== path.dirname(dir); depth++, dir = path.dirname(dir)) {
      const key = dir.toUpperCase();
      if (!folderRegos.has(key)) folderRegos.set(key, new Set());
      folderRegos.get(key)!.add(rego.toUpperCase());
    }
  }

  const result = new Map<string, string>();
  for (const [dir, regos] of folderRegos) {
    if (regos.size === 1) result.set(dir, [...regos][0]);
  }
  return result;
}

export function matchFile(
  filePath: string,
  caravans: CaravanForMatching[],
  existingLink: { rego: string } | null,
  folderRegoIndex: Map<string, string>,
): FileMatch {
  const fileName = path.parse(filePath).name.toUpperCase();
  const folderPath = path.dirname(filePath).toUpperCase();
  const fileNameWithExt = path.basename(filePath);
  const fileExtension = path.extname(filePath).toLowerCase();
  const fileSizeBytes = fs.existsSync(filePath) ? fs.statSync(filePath).size : 0;

  if (existingLink) {
    const linked = caravans.find((c) => c.registrationNumber === existingLink.rego);
    return {
      filePath,
      fileName: fileNameWithExt,
      fileExtension,
      fileSizeBytes,
      confidence: 1.0,
      matchMethod: "AlreadyLinked",
      suggestedRegistrationNumber: existingLink.rego,
      suggestedCaravanDescription: linked ? describeCaravan(linked) : null,
      alreadyLinked: true,
    };
  }

  const best = {
    confidence: 0,
    suggestedRegistrationNumber: null as string | null,
    suggestedCaravanDescription: null as string | null,
    matchMethod: "",
  };

  for (const caravan of caravans) {
    tryMatch(caravan, fileName, folderPath, best);
  }

  // Tier 3.5: same vehicle-folder as an already-linked document
  if (best.confidence < 0.68) {
    let dir: string | null = path.dirname(filePath);
    for (let depth = 0; depth < 5 && dir !== null; depth++, dir = path.dirname(dir)) {
      const inferredRego = folderRegoIndex.get(dir.toUpperCase());
      if (inferredRego) {
        const c = caravans.find((x) => x.registrationNumber === inferredRego);
        const conf = depth <= 1 ? 0.68 : 0.62;
        if (conf > best.confidence) {
          best.suggestedRegistrationNumber = inferredRego;
          best.suggestedCaravanDescription = c ? describeCaravan(c) : inferredRego;
          best.confidence = conf;
          best.matchMethod = "SameFolderInference";
        }
        break;
      }
      if (path.dirname(dir) === dir) break; // reached filesystem root
    }
  }

  return {
    filePath,
    fileName: fileNameWithExt,
    fileExtension,
    fileSizeBytes,
    confidence: best.confidence,
    matchMethod: best.matchMethod,
    suggestedRegistrationNumber: best.suggestedRegistrationNumber,
    suggestedCaravanDescription: best.suggestedCaravanDescription,
    alreadyLinked: false,
  };
}
