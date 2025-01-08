module Client.IO.ProjectOperations

open Client

let projectFiles = [ "readme.md"; "notes.md"; "todo.md" ]

let folderFilters: (string -> bool) list =
    [ (_.StartsWith('.') >> not)
      (_.Equals("bin") >> not)
      (_.Equals("obj") >> not) ]

let impl root =
    { ProjectData.Root = root
      FolderFilters = folderFilters
      SpecialFiles = projectFiles }
