/**
 * Same quote-chain markers as the Outlook add-in's client-side trimmer
 * (public/addin/taskpane.js) and the old ConversationsController's
 * cleanup-quoted-chains endpoint. Each nesting level of a reply chain is
 * indented with leading tabs/spaces (Apple/iPhone Mail does this heavily),
 * so every marker tolerates leading whitespace rather than anchoring
 * straight at line-start.
 */
const REPLY_CHAIN_PATTERNS = [
  /^[ \t]*_{10,}\s*$/m,
  /^[ \t]*-{5,}\s*Original Message\s*-{5,}\s*$/im,
  /^[ \t]*-{5,}\s*Forwarded Message\s*-{5,}\s*$/im,
  /^[ \t]*From:\s.+\r?\n[ \t]*(?:Sent|Date):\s.+$/im,
  /^[ \t]*On .{5,100} wrote:\s*$/im,
];

export function trimQuotedReplyChain(bodyText: string): string {
  let cutIndex = bodyText.length;
  for (const pattern of REPLY_CHAIN_PATTERNS) {
    const match = pattern.exec(bodyText);
    if (match && match.index < cutIndex) cutIndex = match.index;
  }
  return bodyText.slice(0, cutIndex).trimEnd();
}
