using Trinity.Editor;
using Xunit;

namespace Trinity.Tests;

/// <summary>Which trash a deleted file goes to, so files on another drive can be trashed too.</summary>
public sealed class TrashTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("trinity-trash-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    const string Mounts = """
        /dev/nvme0n1p2 / ext4 rw,relatime 0 0
        /dev/nvme0n1p3 /home ext4 rw,relatime 0 0
        /dev/sda1 /mnt/games ext4 rw,relatime 0 0
        /dev/sdb1 /mnt/my\040drive btrfs rw 0 0
        tmpfs /tmp tmpfs rw 0 0
        """;

    [Theory]
    [InlineData("/home/blomst/Projects/game/Assets/rock.glb", "/home")]
    [InlineData("/mnt/games/trinity/Scenes", "/mnt/games")]
    [InlineData("/mnt/gamesx/file", "/")] // a name that only starts like a mount point isn't on it
    [InlineData("/mnt/my drive/a.png", "/mnt/my drive")] // \040 in the table is a space
    [InlineData("/etc/hosts", "/")]
    public void Finds_the_drive_a_path_is_on(string path, string mount) =>
        Assert.Equal(mount, ProjectFiles.MountPointOf(path, Mounts));

    [Fact]
    public void Same_drive_uses_the_users_trash_another_drive_its_own()
    {
        var (home, homePath) = ProjectFiles.TrashFor("/home/blomst/game/rock.glb", "/home/blomst/.local/share", Mounts, "1000");
        Assert.Equal("/home/blomst/.local/share/Trash", home);
        Assert.Equal("/home/blomst/game/rock.glb", homePath);

        var (drive, drivePath) = ProjectFiles.TrashFor("/mnt/games/game/Assets", "/home/blomst/.local/share", Mounts, "1000");
        Assert.Equal("/mnt/games/.Trash-1000", drive);
        Assert.Equal("game/Assets", drivePath); // relative to the drive's top, as the spec says
    }

    [Fact]
    public void A_file_on_another_drive_goes_to_that_drives_trash()
    {
        // Two folders under one temporary folder, with a mount table saying one of them is another drive
        var home = Path.Combine(_dir, "home");
        var drive = Path.Combine(_dir, "drive");
        Directory.CreateDirectory(Path.Combine(home, "data"));
        Directory.CreateDirectory(Path.Combine(drive, "game", "Assets"));
        var file = Path.Combine(drive, "game", "Assets", "rock.glb");
        File.WriteAllText(file, "model");
        var mounts = $"/dev/a {home} ext4 rw 0 0\n/dev/b {drive} ext4 rw 0 0\n";

        var where = ProjectFiles.Trash(file, Path.Combine(home, "data"), mounts, "1000");
        Assert.False(File.Exists(file));
        Assert.Equal(Path.Combine(drive, ".Trash-1000", "files", "rock.glb"), where);
        var info = File.ReadAllText(Path.Combine(drive, ".Trash-1000", "info", "rock.glb.trashinfo"));
        Assert.Contains("Path=game/Assets/rock.glb", info);
    }
}
