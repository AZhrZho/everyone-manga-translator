using MangaTranslator.ProjectSystem.Local;

namespace MangaTranslator.ProjectSystem.Local.Tests;

public sealed class ProjectEditLockTests
{
    [Fact]
    public void ExportLeaseLocksOnlyTargetProjectAndReleasesOnDispose()
    {
        var editLock = new LocalProjectEditLock();

        using (editLock.AcquireExport("project-a"))
        {
            Assert.True(editLock.IsLocked("project-a"));
            Assert.False(editLock.IsLocked("project-b"));
            Assert.Throws<InvalidOperationException>(() => editLock.AcquireExport("project-a"));
        }

        Assert.False(editLock.IsLocked("project-a"));
        using var secondLease = editLock.AcquireExport("project-a");
        Assert.True(editLock.IsLocked("project-a"));
    }
}
