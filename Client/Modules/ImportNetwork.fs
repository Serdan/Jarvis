module Client.ImportNetwork

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Sockets
open System.Security.Cryptography
open System.Threading
open System.Threading.Tasks
open Client

// A URL supplied through ChatGPT's openai/fileParams is still data from a
// caller. Its file_id and MIME type are not an authentication mechanism.
type SourceKind =
    | ProviderAttachment
    | OtherPublicHttps of host: string

type RedirectTrust =
    | ProviderOriginsOnly
    | AnyPublicHttps

type DownloadReceipt =
    { BytesWritten: int64
      Sha256: string }

let private providerHost (host: string) =
    host.EndsWith(".oaiusercontent.com", StringComparison.OrdinalIgnoreCase)
    || String.Equals(host, "oaiusercontent.com", StringComparison.OrdinalIgnoreCase)

// RFC1918, localhost, link-local, carrier NAT, benchmarking, documentation,
// multicast, protocol assignments and other non-global IPv4 space are never
// valid download destinations, even after local permission confirmation.
let rec isPublicAddress (address: IPAddress) =
    if address.IsIPv4MappedToIPv6 then
        isPublicAddress (address.MapToIPv4())
    else
        match address.AddressFamily with
        | AddressFamily.InterNetwork ->
            let b = address.GetAddressBytes()
            let a, n, c = int b[0], int b[1], int b[2]
            a >= 1 && a <= 223
            && a <> 10 && a <> 127
            && not (a = 100 && n >= 64 && n <= 127)
            && not (a = 169 && n = 254)
            && not (a = 172 && n >= 16 && n <= 31)
            && not (a = 192 && (n = 0 || n = 88 && c = 99 || n = 168))
            && not (a = 198 && (n = 18 || n = 19 || n = 51 && c = 100))
            && not (a = 203 && n = 0 && c = 113)
        | AddressFamily.InterNetworkV6 ->
            let b = address.GetAddressBytes()
            // Only global unicast is eligible. Exclude documentation,
            // Teredo, ORCHID, and 6to4 (which embeds unchecked IPv4).
            (int b[0] &&& 0xE0) = 0x20
            && not (b[0] = 0x20uy && b[1] = 0x01uy
                    && (b[2] = 0x00uy && b[3] = 0x00uy // Teredo
                        || b[2] = 0x0duy && b[3] = 0xb8uy // documentation
                        || b[2] = 0x00uy && b[3] >= 0x20uy && b[3] <= 0x2fuy)) // ORCHID
            && not (b[0] = 0x20uy && b[1] = 0x02uy) // 6to4
        | _ -> false

let parseUrl (url: string) : Client.Result<Uri> =
    match Uri.TryCreate(url, UriKind.Absolute) with
    | false, _ -> Error(ValidationError "Download URL must be absolute HTTPS.")
    | true, uri when uri.Scheme <> Uri.UriSchemeHttps ->
        Error(ValidationError "Download URL must use HTTPS.")
    | true, uri when not (String.IsNullOrEmpty uri.UserInfo) ->
        Error(ValidationError "Download URL must not contain embedded credentials.")
    | true, uri when not (String.IsNullOrEmpty uri.Fragment) ->
        Error(ValidationError "Download URL must not contain a fragment.")
    | true, uri when String.IsNullOrWhiteSpace uri.IdnHost ->
        Error(ValidationError "Download URL has no host.")
    | true, uri ->
        match IPAddress.TryParse(uri.IdnHost) with
        | true, address when not (isPublicAddress address) ->
            Error(ValidationError "Download URL targets a non-public IP address.")
        | _ -> Ok uri

let classifyUrl url : Client.Result<SourceKind> =
    parseUrl url
    |> Result.map (fun uri ->
        if providerHost uri.IdnHost && uri.Port = 443 then ProviderAttachment
        else OtherPublicHttps uri.IdnHost)

let isKnownAttachment url =
    match classifyUrl url with
    | Ok ProviderAttachment -> true
    | _ -> false

let private checkRedirect trust (uri: Uri) =
    match trust with
    | ProviderOriginsOnly when not (providerHost uri.IdnHost && uri.Port = 443) ->
        Error(ValidationError "Attachment redirected outside the trusted file-delivery domain.")
    | _ -> Ok uri

let private publicAddresses (addresses: IPAddress[]) =
    if isNull addresses || addresses.Length = 0 then
        Error(ValidationError "Download host has no DNS addresses.")
    elif addresses |> Array.exists (isPublicAddress >> not) then
        Error(PermissionDenied "Download host resolved to a non-public network address.")
    else Ok addresses

// The resolution is performed once per connection, then the actual socket
// receives the vetted *IP address*, never the hostname. This prevents DNS
// rebinding between checking an address and connecting to it.
let rec private connectVetted
    (connect: IPAddress -> int -> CancellationToken -> Task<Stream>)
    (addresses: IPAddress[]) port (token: CancellationToken) index : Task<Stream> =
    task {
        if index >= addresses.Length then
            return raise (HttpRequestException "Unable to connect to the download host.")
        else
            try return! connect addresses[index] port token
            with
            | :? OperationCanceledException -> return raise (OperationCanceledException())
            | _ -> return! connectVetted connect addresses port token (index + 1)
    }

// ConnectCallback receives DNS address bytes that were checked by the caller,
// not a hostname to resolve again (no validation/connect rebinding gap).
let resolveAndConnect
    (resolve: string -> CancellationToken -> Task<IPAddress[]>)
    (connect: IPAddress -> int -> CancellationToken -> Task<Stream>)
    (host: string) port (token: CancellationToken) : Task<Stream> =
    task {
        let! addresses =
            match IPAddress.TryParse host with
            | true, ip -> Task.FromResult([| ip |])
            | _ -> resolve host token
        match publicAddresses addresses with
        | Error _ -> return raise (HttpRequestException "The download host has a non-public network address.")
        | Ok addresses ->
            return! connectVetted connect addresses port token 0
    }

let private connectSocket (ip: IPAddress) (port: int) (token: CancellationToken) : Task<Stream> =
    task {
        let socket = new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
        try
            do! socket.ConnectAsync(IPEndPoint(ip, port), token)
            return new NetworkStream(socket, ownsSocket = true) :> Stream
        with ex ->
            socket.Dispose()
            return raise ex
    }

let private createClient () =
    let handler = new SocketsHttpHandler()
    handler.AllowAutoRedirect <- false
    handler.UseProxy <- false
    handler.UseCookies <- false
    handler.AutomaticDecompression <- DecompressionMethods.None
    handler.PooledConnectionLifetime <- TimeSpan.FromMinutes 1.
    handler.ConnectCallback <-
        Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>(fun context token ->
            ValueTask<Stream>(
                resolveAndConnect
                    (fun host ct -> Dns.GetHostAddressesAsync(host, ct))
                    connectSocket
                    context.DnsEndPoint.Host
                    context.DnsEndPoint.Port
                    token))
    let http = new HttpClient(handler, disposeHandler = true)
    // A linked per-import token limits the entire response body and redirect
    // chain, not just the headers (ResponseHeadersRead bypasses HttpClient.Timeout).
    http.Timeout <- Timeout.InfiniteTimeSpan
    http

// Exactly one transport/handler and connection pool per Jarvis client process.
let private transport = lazy (createClient())
let sharedClient () = transport.Value

let private isRedirect status =
    match int status with
    | 301 | 302 | 303 | 307 | 308 -> true
    | _ -> false

let private getResponse (http: HttpClient) trust (first: Uri) (token: CancellationToken) =
    let rec next (uri: Uri) redirects = task {
        use request = new HttpRequestMessage(HttpMethod.Get, uri)
        let! response = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
        if isRedirect response.StatusCode then
            let location = response.Headers.Location
            response.Dispose()
            if redirects >= 5 then
                return Error(ValidationError "Download exceeded the redirect limit.")
            elif isNull location then
                return Error(ValidationError "Download redirect has no Location header.")
            else
                let destination = Uri(uri, location)
                match parseUrl destination.AbsoluteUri |> Result.bind (checkRedirect trust) with
                | Error failure -> return Error failure
                | Ok verifiedUri -> return! next verifiedUri (redirects + 1)
        elif response.IsSuccessStatusCode then
            return Ok response
        else
            let status = int response.StatusCode
            response.Dispose()
            return Error(ValidationError $"Download failed with HTTP status {status}.")
    }
    next first 0

let downloadTo (http: HttpClient) trust (url: string) (stage: string)
               maxBytes (token: CancellationToken) : Task<Client.Result<DownloadReceipt>> =
    task {
        try
            match parseUrl url |> Result.bind (checkRedirect trust) with
            | Error failure -> return Error failure
            | Ok uri ->
                use timeout = CancellationTokenSource.CreateLinkedTokenSource(token)
                timeout.CancelAfter(TimeSpan.FromMinutes 2.)
                let! responseResult = getResponse http trust uri timeout.Token
                match responseResult with
                | Error failure -> return Error failure
                | Ok response ->
                    use response = response
                    let length = response.Content.Headers.ContentLength
                    if length.HasValue && length.Value > maxBytes then
                        return Error(ValidationError $"File exceeds the {maxBytes} byte import limit.")
                    else
                        let! source = response.Content.ReadAsStreamAsync(timeout.Token)
                        use source = source
                        let settings = new FileStreamOptions()
                        settings.Mode <- FileMode.CreateNew
                        settings.Access <- FileAccess.Write
                        settings.Share <- FileShare.None
                        settings.BufferSize <- 81920
                        settings.Options <- FileOptions.Asynchronous ||| FileOptions.SequentialScan
                        if OperatingSystem.IsLinux() || OperatingSystem.IsMacOS() then
                            // Set mode at creation, not in a separate chmod
                            // after a world-readable staging window.
                            settings.UnixCreateMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite
                        use destination = new FileStream(stage, settings)
                        use hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256)
                        let buffer = Array.zeroCreate<byte> 81920
                        let mutable total = 0L
                        let mutable finished = false
                        while not finished do
                            let! count = source.ReadAsync(buffer.AsMemory(), timeout.Token)
                            if count = 0 then finished <- true
                            else
                                if int64 count > maxBytes - total then
                                    raise (InvalidDataException $"Downloaded file exceeds the {maxBytes} byte import limit.")
                                do! destination.WriteAsync(buffer.AsMemory(0, count), timeout.Token)
                                hash.AppendData(buffer, 0, count)
                                total <- total + int64 count
                        destination.Flush(true)
                        let digest =
                            hash.GetHashAndReset() |> Convert.ToHexString |> fun s -> $"sha256:{s.ToLowerInvariant()}"
                        return Ok { BytesWritten = total; Sha256 = digest }
        with
        | :? OperationCanceledException -> return Error(ValidationError "Download timed out or was canceled.")
        | :? HttpRequestException -> return Error(ValidationError "Secure HTTPS download failed.")
        | :? InvalidDataException as ex -> return Error(ValidationError ex.Message)
        | :? IOException -> return Error(ValidationError "Unable to write imported file.")
        | :? UriFormatException -> return Error(ValidationError "Invalid HTTPS redirect URI.")
        | :? ArgumentException -> return Error(ValidationError "Invalid HTTPS download request.")
        // Network exceptions can include the complete request URI, whose
        // query may contain a short-lived bearer signature. Never forward
        // unknown exception messages into command/activity logs.
        | _ -> return Error(ValidationError "Secure HTTPS download could not be completed.")
    }
