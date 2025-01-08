module Client.IO.FileIO

open Client

let getFullPath path (rt: #FileIO) = rt.File.getFullPath path
let parseFile file (rt: #FileIO) = rt.File.parseFile file
let parseFolder path (rt: #FileIO) = rt.File.parseFolder path
let getChildFolders path (rt: #FileIO) = rt.File.getChildFolders path
let getFolderName path (rt: #FileIO) = rt.File.GetFolderName path
let getFileName file (rt: #FileIO) = rt.File.getFileName file
let getFiles path (rt: #FileIO) = rt.File.GetFiles path
let getFileInfo file (rt: #FileIO) = rt.File.GetFileInfo file
let readAllText file (rt: #FileIO) = rt.File.ReadAllText file
let writeAllText filePath content (rt: #FileIO) = rt.File.WriteAllText filePath content
let appendAllText file content (rt: #FileIO) = rt.File.AppendAllText file content
