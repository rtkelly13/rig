"""Unit tests for estate system apps and downloads modules."""

from __future__ import annotations

import datetime as dt
import pathlib

import pytest

from rig.estate import apps, downloads
from tests.links import dangling_dir_link


@pytest.mark.ci
def test_download_classification(tmp_path: pathlib.Path) -> None:
    installer = tmp_path / "app.dmg"
    installer.touch()
    assert downloads.classify_path(installer) == downloads.DownloadCategory.INSTALLER

    dataset = tmp_path / "data.parquet"
    dataset.touch()
    assert downloads.classify_path(dataset) == downloads.DownloadCategory.DATASET

    image = tmp_path / "photo.jpg"
    image.touch()
    assert downloads.classify_path(image) == downloads.DownloadCategory.MEDIA

    archive = tmp_path / "files.zip"
    archive.touch()
    assert downloads.classify_path(archive) == downloads.DownloadCategory.ARCHIVE


@pytest.mark.ci
def test_scan_downloads_finds_duplicates(tmp_path: pathlib.Path) -> None:
    dl_dir = tmp_path / "Downloads"
    dl_dir.mkdir()
    cloud_root = tmp_path / "Cloud"
    cloud_root.mkdir()

    # Create a duplicate file
    dl_file = dl_dir / "report.pdf"
    dl_file.write_text("content" * 100)
    cloud_file = cloud_root / "report.pdf"
    cloud_file.write_text("content" * 100)

    # Create a unique file
    unique_file = dl_dir / "notes.txt"
    unique_file.write_text("unique" * 10)

    items = downloads.scan_downloads(
        downloads_dir=dl_dir,
        min_size=10,
        cloud_roots=[cloud_root],
    )

    names = {item.name: item for item in items}
    assert "report.pdf" in names
    assert names["report.pdf"].has_cloud_duplicate is True
    assert names["report.pdf"].cloud_duplicate_path == cloud_file

    assert "notes.txt" in names
    assert names["notes.txt"].has_cloud_duplicate is False


@pytest.mark.ci
def test_scan_applications_parses_fake_app(tmp_path: pathlib.Path) -> None:
    apps_dir = tmp_path / "Applications"
    apps_dir.mkdir()

    fake_app = apps_dir / "FakeApp.app"
    fake_app.mkdir()
    contents = fake_app / "Contents"
    contents.mkdir()

    # Create dummy Info.plist
    plist = contents / "Info.plist"
    plist.write_text("dummy plist")

    # Put a dummy binary inside to give it size
    bin_file = contents / "binary"
    bin_file.write_text("x" * 2000)

    app_list = apps.scan_applications([apps_dir], min_size=100)
    assert len(app_list) == 1
    assert app_list[0].name == "FakeApp.app"
    assert app_list[0].path == fake_app


@pytest.mark.ci
def test_app_dormant_property(tmp_path: pathlib.Path) -> None:
    app_path = tmp_path / "OldApp.app"
    app_path.mkdir()

    now = dt.datetime.now(dt.UTC)
    old_time = now - dt.timedelta(days=200)

    dormant_app = apps.AppInfo(
        name="OldApp.app",
        path=app_path,
        bytes_used=1024,
        version="1.0.0",
        modified=old_time,
        accessed=old_time,
    )
    assert dormant_app.is_dormant is True

    recent_time = now - dt.timedelta(days=10)
    active_app = apps.AppInfo(
        name="RecentApp.app",
        path=app_path,
        bytes_used=1024,
        version="1.0.0",
        modified=recent_time,
        accessed=recent_time,
    )
    assert active_app.is_dormant is False


@pytest.mark.ci
def test_cloud_duplicate_requires_matching_content(tmp_path: pathlib.Path) -> None:
    dl_dir = tmp_path / "Downloads"
    dl_dir.mkdir()
    cloud = tmp_path / "Cloud"
    cloud.mkdir()
    (dl_dir / "a.bin").write_bytes(b"A" * 100)
    (cloud / "a.bin").write_bytes(b"B" * 100)  # same name and size, different bytes
    folder = dl_dir / "set"
    folder.mkdir()
    (folder / "f.txt").write_bytes(b"1" * 50)
    cloud_folder = cloud / "set"
    cloud_folder.mkdir()
    (cloud_folder / "f.txt").write_bytes(b"2" * 50)

    items = {i.name: i for i in downloads.scan_downloads(dl_dir, min_size=10, cloud_roots=[cloud])}
    assert items["a.bin"].has_cloud_duplicate is False
    assert items["set"].has_cloud_duplicate is False

    (cloud / "a.bin").write_bytes(b"A" * 100)
    (cloud_folder / "f.txt").write_bytes(b"1" * 50)
    items = {i.name: i for i in downloads.scan_downloads(dl_dir, min_size=10, cloud_roots=[cloud])}
    assert items["a.bin"].has_cloud_duplicate is True
    assert items["set"].has_cloud_duplicate is True


@pytest.mark.ci
def test_scan_downloads_skips_broken_symlink(tmp_path: pathlib.Path) -> None:
    dl_dir = tmp_path / "Downloads"
    dl_dir.mkdir()
    dangling_dir_link(dl_dir / "dangling", tmp_path / "missing")
    (dl_dir / "real.bin").write_bytes(b"x" * 100)
    items = downloads.scan_downloads(dl_dir, min_size=10, cloud_roots=[])
    assert [i.name for i in items] == ["real.bin"]


@pytest.mark.ci
def test_dormancy_considers_access_time(tmp_path: pathlib.Path) -> None:
    now = dt.datetime.now(dt.UTC)
    app = apps.AppInfo(
        name="X.app",
        path=tmp_path,
        bytes_used=1,
        version="1",
        modified=now - dt.timedelta(days=400),
        accessed=now - dt.timedelta(days=5),
    )
    assert app.is_dormant is False


@pytest.mark.ci
def test_scan_applications_keeps_same_name_in_different_dirs(tmp_path: pathlib.Path) -> None:
    for d, n in (("sys", 100), ("user", 200_000)):
        bundle = tmp_path / d / "Foo.app" / "Contents"
        bundle.mkdir(parents=True)
        (bundle / "bin").write_bytes(b"x" * n)
    found = apps.scan_applications([tmp_path / "sys", tmp_path / "user"], min_size=100_000)
    assert [a.path.parent.name for a in found] == ["user"]
    found = apps.scan_applications([tmp_path / "sys", tmp_path / "user"], min_size=1)
    assert len(found) == 2
