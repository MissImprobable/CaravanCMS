import { centsToDecimal, hundredthsToDecimal } from "./money";
import { toIso } from "./dates";
import type {
  CustomerRow,
  CaravanRow,
  JobRow,
  InvoiceRow,
  InvoiceItemRow,
  DocumentRow,
  ConversationRow,
  CommunicationLogRow,
  TagRow,
} from "./rows";

export function toCustomerDto(c: CustomerRow) {
  return {
    id: c.Id,
    name: c.Name,
    email: c.Email,
    phone: c.Phone,
    mobile: c.Mobile,
    address: c.Address,
    suburb: c.Suburb,
    state: c.State,
    postcode: c.Postcode,
    customerNumber: c.CustomerNumber,
    mechanicDeskId: c.MechanicDeskId,
    createdAt: toIso(c.CreatedAt),
  };
}

export function toCustomerLookupDto(c: CustomerRow, caravanCount: number, jobCount: number) {
  return {
    id: c.Id,
    name: c.Name,
    email: c.Email,
    phone: c.Phone,
    mobile: c.Mobile,
    customerNumber: c.CustomerNumber,
    caravanCount,
    jobCount,
  };
}

/** Port of CaravanSummaryDto.DisplayText — "ABC123  Make Model  Customer", skipping any blank parts. */
function buildDisplayText(registrationNumber: string, make: string | null, model: string | null, customerName: string | null): string {
  const makeModel = `${make ?? ""} ${model ?? ""}`.trim();
  return [registrationNumber, makeModel, customerName].filter((s) => s && s.trim().length > 0).join("  ");
}

export function toCaravanSummaryDto(
  c: CaravanRow,
  customer: { name: string | null; phone: string | null; email: string | null },
  jobCount: number,
  documentCount: number,
) {
  return {
    registrationNumber: c.RegistrationNumber,
    vin: c.Vin,
    make: c.Make,
    model: c.Model,
    year: c.Year,
    color: c.Color,
    body: c.Body,
    currentOdometer: c.CurrentOdometer,
    lastJobDate: toIso(c.LastJobDate),
    selfContainment: c.SelfContainment,
    selfContainmentDue: toIso(c.SelfContainmentDue),
    customerName: customer.name,
    customerPhone: customer.phone,
    customerEmail: customer.email,
    customerId: c.CustomerId,
    jobCount,
    documentCount,
    createdAt: toIso(c.CreatedAt),
    updatedAt: toIso(c.UpdatedAt),
    displayText: buildDisplayText(c.RegistrationNumber, c.Make, c.Model, customer.name),
  };
}

export function toTagDto(t: TagRow) {
  return { id: t.Id, name: t.Name };
}

export function toCommunicationLogDto(m: CommunicationLogRow) {
  return {
    id: m.Id,
    conversationId: m.ConversationId,
    type: m.Type,
    direction: m.Direction,
    fromAddress: m.FromAddress,
    body: m.Body,
    loggedBy: m.LoggedBy,
    occurredAt: toIso(m.OccurredAt),
  };
}

export function toConversationDto(conv: ConversationRow, tags: TagRow[], messages: CommunicationLogRow[]) {
  return {
    id: conv.Id,
    customerId: conv.CustomerId,
    registrationNumber: conv.RegistrationNumber,
    subject: conv.Subject,
    externalConversationId: conv.ExternalConversationId,
    startedAt: toIso(conv.StartedAt),
    lastActivityAt: toIso(conv.LastActivityAt),
    tags: [...tags].sort((a, b) => a.Name.localeCompare(b.Name)).map(toTagDto),
    messages: [...messages]
      .sort((a, b) => a.OccurredAt.localeCompare(b.OccurredAt))
      .map(toCommunicationLogDto),
  };
}

export function toInvoiceItemDto(ii: InvoiceItemRow) {
  return {
    id: ii.Id,
    invoiceId: ii.InvoiceId,
    description: ii.Description,
    category: ii.Category,
    unitPrice: centsToDecimal(ii.UnitPriceCents),
    quantity: hundredthsToDecimal(ii.QuantityHundredths),
    netAmount: centsToDecimal(ii.NetAmountCents),
    taxAmount: centsToDecimal(ii.TaxAmountCents),
    stockNumber: ii.StockNumber,
  };
}

export function toInvoiceDto(i: InvoiceRow, items: InvoiceItemRow[]) {
  return {
    id: i.Id,
    jobId: i.JobId,
    customerId: i.CustomerId,
    registrationNumber: i.RegistrationNumber,
    invoiceNumber: i.InvoiceNumber,
    issueDate: toIso(i.IssueDate),
    dueDate: toIso(i.DueDate),
    netAmount: centsToDecimal(i.NetAmountCents),
    taxAmount: centsToDecimal(i.TaxAmountCents),
    totalAmount: centsToDecimal(i.TotalAmountCents),
    paidAmount: centsToDecimal(i.PaidAmountCents),
    balanceDue: centsToDecimal(i.TotalAmountCents - i.PaidAmountCents),
    status: i.Status,
    mechanicDeskId: i.MechanicDeskId,
    createdAt: toIso(i.CreatedAt),
    items: items.map(toInvoiceItemDto),
  };
}

export function toJobDetailDto(j: JobRow, invoices: ReturnType<typeof toInvoiceDto>[]) {
  return {
    id: j.Id,
    registrationNumber: j.RegistrationNumber,
    customerId: j.CustomerId,
    jobNumber: j.JobNumber,
    status: j.Status,
    jobType: j.JobType,
    description: j.Description,
    notes: j.Notes,
    startDate: toIso(j.StartDate),
    finishDate: toIso(j.FinishDate),
    estimatedHours: j.EstimatedHours,
    finishedBy: j.FinishedBy,
    mechanicDeskId: j.MechanicDeskId,
    createdAt: toIso(j.CreatedAt),
    updatedAt: toIso(j.UpdatedAt),
    invoices,
  };
}

export function toDocumentDto(d: DocumentRow) {
  return {
    id: d.Id,
    registrationNumber: d.RegistrationNumber,
    documentType: d.DocumentType,
    category: d.Category,
    filePath: d.FilePath,
    fileName: d.FileName,
    uploadedDate: toIso(d.UploadedDate),
    isLocalPath: !!d.IsLocalPath,
    mimeType: d.MimeType,
    notes: d.Notes,
    linkMethod: d.LinkMethod,
    createdAt: toIso(d.CreatedAt),
  };
}
