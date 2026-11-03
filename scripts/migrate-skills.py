#!/usr/bin/env python3
"""Move legacy Jarvis skills into standard .agents/skills/ directories.

Dry-run by default. Use --apply only after reviewing proposed moves.
Existing destination directories and symlinks are never overwritten.
"""
import argparse
from pathlib import Path
import shutil


def migrate_root(root: Path, apply: bool) -> tuple[int, int]:
    source = root / ".jarvis" / "skills"
    target = root / ".agents" / "skills"
    if not source.is_dir() or source.is_symlink() or (root / ".jarvis").is_symlink():
        return (0, 0)
    moved = conflicts = 0
    for skill in sorted(source.iterdir()):
        if skill.is_symlink() or not skill.is_dir():
            print(f"SKIP unsafe entry: {skill}")
            conflicts += 1
            continue
        if any(child.is_symlink() for child in skill.rglob("*")):
            print(f"SKIP nested symlink: {skill}")
            conflicts += 1
            continue
        if not any((skill / filename).is_file() for filename in ("SKILL.md", "skill.md")):
            print(f"SKIP missing skill file: {skill}")
            conflicts += 1
            continue
        destination = target / skill.name
        if target.is_symlink() or (root / ".agents").is_symlink() or destination.exists() or destination.is_symlink():
            print(f"SKIP existing or unsafe destination: {destination}")
            conflicts += 1
            continue
        print(f"{'MOVE' if apply else 'WOULD MOVE'} {skill} -> {destination}")
        if apply:
            target.mkdir(parents=True, exist_ok=True)
            shutil.move(str(skill), str(destination))
        moved += 1
    return moved, conflicts


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("workspace", type=Path, help="Workspace containing project directories and global skills")
    parser.add_argument("--apply", action="store_true", help="Perform the moves (default: dry run)")
    args = parser.parse_args()
    workspace = args.workspace.expanduser().resolve(strict=True)
    roots = [workspace] + [
        path for path in sorted(workspace.iterdir())
        if path.is_dir() and not path.is_symlink() and not path.name.startswith(".")
    ]
    moves = conflicts = 0
    for root in roots:
        count, skipped = migrate_root(root, args.apply)
        moves += count
        conflicts += skipped
    print(f"{'Moved' if args.apply else 'Proposed'}: {moves}; skipped: {conflicts}")


if __name__ == "__main__":
    main()
