module Client.IO.ProjectIO

open Client

let root (rt: #ProjectIO) = rt.Project.Root |> Ok
let folderFilters (rt: #ProjectIO) = rt.Project.FolderFilters
let specialFiles (rt: #ProjectIO) = rt.Project.SpecialFiles |> Ok
