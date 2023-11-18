using JarvisClient;
using JarvisClientTests.Impl;

namespace JarvisClientTests;

[TestClass]
public class ListProjectsTests
{
    [TestMethod]
    public void TestListProjects()
    {
        var fileSystem = new FakeFileSystem();
        fileSystem.AddFolder(@"C:\repos", "Project1");

        var projectBrowser = new ProjectBrowser(fileSystem, @"C:\repos");

        var projects = projectBrowser.ListProjects();

        Assert.AreEqual("Project1", projects[0]);
    }
}
