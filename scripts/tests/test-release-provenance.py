#!/usr/bin/env python3
"""Black-box release provenance tests. Uses only temporary local Git repositories."""
import os
from pathlib import Path
import subprocess
import sys
import tempfile


SCRIPT = Path(__file__).resolve().parents[1] / "release-provenance.cs"


def execute(root: Path, *args: str, succeeds: bool = True) -> subprocess.CompletedProcess[str]:
    command = ["dotnet", str(SCRIPT), *map(str, args)]
    result = subprocess.run(command, cwd=root, capture_output=True, text=True, timeout=90)
    if (result.returncode == 0) != succeeds:
        raise AssertionError(
            f"Command {command!r} expected success={succeeds}, got {result.returncode}\n"
            f"stdout:\n{result.stdout[-4000:]}\nstderr:\n{result.stderr[-4000:]}"
        )
    return result


def git(root: Path, *args: str) -> str:
    result = subprocess.run(["git", *args], cwd=root, capture_output=True,
                            text=True, check=True, timeout=10)
    return result.stdout.strip()


def check() -> None:
    with tempfile.TemporaryDirectory(prefix="jarvis-release-provenance-") as directory:
        root = Path(directory)
        (root / "Jarvis.slnx").write_text("<Solution />\n", encoding="utf-8")
        (root / ".gitignore").write_text("artifacts/\n", encoding="utf-8")
        (root / "README.md").write_text("fixture\n", encoding="utf-8")
        for project in ("Client", "Client.Tests", "Common", "Server", "Server.Tests"):
            project_dir = root / project
            project_dir.mkdir()
            (project_dir / "packages.lock.json").write_text("{}\n", encoding="utf-8")
        git(root, "init", "-q")
        git(root, "config", "user.email", "fixture@example.invalid")
        git(root, "config", "user.name", "Release Test")
        git(root, "add", ".")
        git(root, "commit", "-qm", "fixture")
        head = git(root, "rev-parse", "HEAD")

        execute(root, "preflight", head)
        execute(root, "verify", succeeds=False)
        execute(root, "preflight", "0" * 40, succeeds=False)

        cases = [
            ("client", "linux-x64", "JarvisClient", "JarvisClient-linux-x64"),
            ("client", "win-x64", "JarvisClient.exe", "JarvisClient-win-x64.exe"),
            ("client", "osx-arm64", "JarvisClient", "JarvisClient-osx-arm64"),
            ("server", "linux-x64", "JarvisServer", "JarvisServer-linux-x64"),
        ]
        for kind, rid, binary_name, download_name in cases:
            source_dir = root / "artifacts" / kind / rid
            source_dir.mkdir(parents=True, exist_ok=True)
            (source_dir / binary_name).write_bytes((kind + "-" + rid).encode("ascii"))
            url = "https://example.invalid/client" if kind == "client" else "-"
            rel_dir = str(source_dir.relative_to(root))
            rel_binary = str((source_dir / binary_name).relative_to(root))
            execute(root, "stamp", kind, rid, "Release", rel_dir, url, head)
            execute(root, "collect", rel_binary, download_name)
            execute(root, "verify")
        execute(root, "verify", "--complete")

        downloads = root / "artifacts" / "downloads"
        manifest = downloads / "RELEASE-PROVENANCE.json"
        sums = downloads / "SHA256SUMS"
        assert manifest.exists() and sums.exists()
        assert "RELEASE-PROVENANCE.json" in sums.read_text(encoding="utf-8")

        binary = downloads / "JarvisClient-linux-x64"
        original = binary.read_bytes()
        binary.write_bytes(original + b"-tampered")
        execute(root, "verify", succeeds=False)
        binary.write_bytes(original)
        execute(root, "verify")

        unexpected = downloads / "extra-file"
        unexpected.write_text("stale", encoding="utf-8")
        execute(root, "verify", succeeds=False)
        unexpected.unlink()
        execute(root, "verify")

        (root / "README.md").write_text("dirty\n", encoding="utf-8")
        execute(root, "preflight", head, succeeds=False)
        execute(root, "collect", "artifacts/client/linux-x64/JarvisClient",
                "JarvisClient-linux-x64", succeeds=False)
        git(root, "checkout", "--", "README.md")
        execute(root, "verify")

        (root / "README.md").write_text("new release\n", encoding="utf-8")
        git(root, "add", "README.md")
        git(root, "commit", "-qm", "new source commit")
        execute(root, "collect", "artifacts/client/linux-x64/JarvisClient",
                "JarvisClient-linux-x64", succeeds=False)
        execute(root, "preflight", head, succeeds=False)

    print("Release provenance black-box tests passed.")


if __name__ == "__main__":
    check()
