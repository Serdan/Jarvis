using Kehlet.Functional;
// ReSharper disable ClassNeverInstantiated.Global

namespace Shared.Messages;

[AutoClosed(true)]
public partial record AgentCommand
{
    partial record ListProjectsCommand;

    partial record GetProjectDetailsCommand(string ProjectName);

    partial record ListProjectDirectoryCommand(string ProjectName, string Path);

    partial record OpenFileCommand(string ProjectName, string Path);

    partial record WriteFileCommand(string ProjectName, string FilePath, string Content, FileWriteMode Mode);

    partial record SectionReplaceCommand(string ProjectName, string FilePath, SectionIdentifiers SectionIdentifiers, string ReplacementContent);

    partial record TextReplaceCommand(string ProjectName, string FilePath, string Search, string Replacement);

    partial record RunDotnetTestCommand(string ProjectName);
}
