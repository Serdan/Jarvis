module Client.ClientConfig
// The linked scheduler's unused default-path helper must never point at live storage.
let configPath () : string = failwith "No live config in this fixture"
