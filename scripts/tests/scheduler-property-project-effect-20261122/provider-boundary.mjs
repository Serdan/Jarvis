// Test-only durable fake provider. Its receipts live outside the project and
// account Git remotes, and survive client ACK loss and object recreation.
import { mkdirSync, existsSync, readFileSync, writeFileSync, renameSync } from "node:fs";
import { dirname } from "node:path";

export class DurableFakeProvider {
  constructor(file) {
    this.file = file;
    mkdirSync(dirname(file), { recursive: true });
    if (!existsSync(file)) writeFileSync(file, JSON.stringify({ revision: 0, sessions: {} }));
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
  send(project, effect, ack = "ackLost") {
    if (project.phase !== "Dispatching" || project.claim?.effect !== effect)
      return { code: "DeniedWithoutCommittedStart" };
    const next = this.read();
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
  verifyNoSession(proof, effect) {
    return proof?.project === "VerifiedNoSend" &&
      proof?.provider === "VerifiedNoProviderSession" &&
      proof?.effect === effect && this.observe(effect) === null;
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
