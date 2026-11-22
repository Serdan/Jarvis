// Test-only durable fake provider. Its receipts live outside the project and
// account Git remotes, and survive client ACK loss and object recreation.
import { mkdirSync, existsSync, readFileSync, writeFileSync, renameSync } from "node:fs";
import { dirname } from "node:path";

export class DurableFakeProvider {
  constructor(file) {
    this.file = file;
    mkdirSync(dirname(file), { recursive: true });
    if (!existsSync(file)) writeFileSync(file, JSON.stringify({
      revision: 0, sessions: {}, noSessionSeals: {}
    }));
  }
  read() { return JSON.parse(readFileSync(this.file, "utf8")); }
  save(next) {
    // A disposable file-backed receipt, atomically replaced before ACK.
    const temp = this.file + ".next";
    writeFileSync(temp, JSON.stringify(next));
    renameSync(temp, this.file);
  }
  observe(effect) {
    const result = this.read().sessions[effect];
    return result ? structuredClone(result) : null;
  }
  noSessionSealed(effect) {
    const state = this.read();
    return !state.sessions[effect] && Number.isInteger(state.noSessionSeals?.[effect]);
  }
  send(project, effect, ack = "ackLost", testMutation = {}) {
    if (project.phase !== "Dispatching" || project.claim?.effect !== effect)
      return { code: "DeniedWithoutCommittedStart" };
    const next = this.read();
    if (Number.isInteger(next.noSessionSeals?.[effect]) && !testMutation.ignoreNoSessionSeal)
      return { code: "DeniedByNoSessionSeal" };
    if (next.sessions[effect])
      return { code: "AlreadySubmitted", receipt: structuredClone(next.sessions[effect]) };
    const receipt = {
      effect, session: "fake-session-1", status: "Active",
      acceptedCount: 1, receiptRevision: next.revision + 1
    };
    next.revision++;
    next.sessions[effect] = receipt;
    this.save(next); // external effect is durable before any ACK is returned
    return { code: ack === "ackLost" ? "Unknown" : "Accepted", receipt: structuredClone(receipt) };
  }
  terminal(effect) {
    const next = this.read();
    if (!next.sessions[effect]) return { code: "UnknownSession" };
    next.revision++;
    next.sessions[effect].status = "Terminal";
    next.sessions[effect].receiptRevision = next.revision;
    this.save(next);
    return { code: "TerminalObserved", receipt: this.observe(effect) };
  }
  // Prospective disposable provider-owned revocation. Absence at observation
  // is not enough: seal and send share one durable provider state transition.
  sealNoSession(effect) {
    const next = this.read();
    if (next.sessions[effect]) return false; // send won
    next.noSessionSeals ??= {};
    if (Number.isInteger(next.noSessionSeals[effect])) return true;
    next.revision++;
    next.noSessionSeals[effect] = next.revision;
    this.save(next); // seal wins, stale snapshots can no longer send
    return true;
  }
  verifyNoSession(proof, effect) {
    return proof?.project === "VerifiedNoSend" &&
      proof?.provider === "VerifiedNoProviderSession" &&
      proof?.effect === effect && this.sealNoSession(effect);
  }
  verifyTerminal(proof, effect) {
    const receipt = this.observe(effect);
    return receipt !== null && receipt.status === "Terminal" &&
      receipt.effect === effect && proof?.marker === "DISPOSABLE_PROVIDER_TERMINAL" &&
      proof?.status === "Terminal" && proof?.effect === effect &&
      proof?.session === receipt.session;
  }
  acceptedCount(effect) { return this.observe(effect)?.acceptedCount ?? 0; }
  restart() { return new DurableFakeProvider(this.file); }
}
