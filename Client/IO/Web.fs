module Client.IO.Web

open Client

let loadPage url (rt: #WebIO) = rt.Browser.LoadPage url
