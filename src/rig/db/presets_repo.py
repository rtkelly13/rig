"""Repository for managing 3D optical simulation presets stored in SQLite."""

from __future__ import annotations

import sqlite3
from dataclasses import dataclass


@dataclass(frozen=True, slots=True)
class OpticalPresetRecord:
    id: int
    name: str
    project: str
    ior: float
    dispersion: float
    roughness: float
    thickness: float
    distortion: float
    caustic_color: str
    created_at: str


def save_optical_preset(
    conn: sqlite3.Connection,
    name: str,
    project: str,
    ior: float,
    dispersion: float,
    roughness: float,
    thickness: float,
    distortion: float,
    caustic_color: str,
    created_at: str,
) -> int:
    """Save a new optical preset into the database."""
    cursor = conn.cursor()
    cursor.execute(
        """
        INSERT INTO optical_presets (
            name, project, ior, dispersion, roughness, thickness, distortion, caustic_color, created_at
        ) VALUES (?, ?, ?, ?, ?, ?, ?, ?, ?)
        """,
        (name, project, ior, dispersion, roughness, thickness, distortion, caustic_color, created_at),
    )
    conn.commit()
    row_id = cursor.lastrowid
    return int(row_id) if row_id is not None else 0


def get_presets_for_project(conn: sqlite3.Connection, project: str) -> list[OpticalPresetRecord]:
    """Retrieve saved optical presets for a given project."""
    cursor = conn.cursor()
    cursor.execute(
        """
        SELECT id, name, project, ior, dispersion, roughness, thickness, distortion, caustic_color, created_at
        FROM optical_presets
        WHERE project = ?
        ORDER BY created_at DESC
        """,
        (project,),
    )
    results: list[OpticalPresetRecord] = []
    for row in cursor.fetchall():
        results.append(
            OpticalPresetRecord(
                id=int(str(row[0])),
                name=str(row[1]),
                project=str(row[2]),
                ior=float(str(row[3])),
                dispersion=float(str(row[4])),
                roughness=float(str(row[5])),
                thickness=float(str(row[6])),
                distortion=float(str(row[7])),
                caustic_color=str(row[8]),
                created_at=str(row[9]),
            )
        )
    return results
