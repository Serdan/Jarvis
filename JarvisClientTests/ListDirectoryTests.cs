using System.Collections.Immutable;
using JarvisClient;
using JarvisClient.Models;
using JarvisClientTests.Impl;

namespace JarvisClientTests;

[TestClass]
public class ListDirectoryTests
{
    [TestMethod]
    public void Test()
    {
        const string root = @"C:\repos";
        const string projectName = "Test Project";
        const string projectPath = $@"{root}\{projectName}";
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFolder(root, projectName);
        fileSystem.AddFolder(projectPath, "Client");

        // Arrange
        var browser = new ProjectBrowser(fileSystem, root);

        // Act
        var result = browser.ListProjectDirectory(projectName, "").IfError(_ => ImmutableArray<ProjectItemKind>.Empty);

        var expected = new ProjectItemKind.ProjectFolder("Client");

        // Assert
        Assert.AreEqual(expected, result.FirstOrDefault());
    }
}
