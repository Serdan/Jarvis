module Client.IO.WebIO

open Client

let loadPage url (rt: #WebIO) = rt.Browser.LoadPage url
