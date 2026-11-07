"""Generic multi-account Git and SSH configuration step."""

from __future__ import annotations

import subprocess
from pathlib import Path

from rich.console import Console
from rich.prompt import Confirm, Prompt

from rig import proc

from ..config.loader import load_user_config, save_user_config
from ..config.models import GitIdentity
from ..engine.context import StepContext

console = Console()


def get_git_config_val(key: str) -> str | None:
    """Read a global git config value."""
    return proc.git_out("config", "--global", key) or None


def set_git_config_val(key: str, val: str) -> None:
    """Set a global git config value."""
    proc.git("config", "--global", key, val)


def apply_conditional_git_includes(identities: list[GitIdentity], dry_run: bool = False) -> None:
    """Configure conditional directory includes in ~/.gitconfig; a dry run reports what it would write."""
    if not identities:
        return

    home = Path.home()
    gitconfig_path = home / ".gitconfig"

    existing_content = gitconfig_path.read_text(encoding="utf-8") if gitconfig_path.is_file() else ""
    updated_content = existing_content

    for ident in identities:
        if not ident.match_directory:
            continue

        profile_file = home / f".gitconfig-{ident.id}"
        profile_content = f"[user]\n\tname = {ident.name}\n\temail = {ident.email}\n"
        if ident.signing_key:
            profile_content += f"\tsigningkey = {ident.signing_key}\n"

        if dry_run:
            console.print(f"[dim]Dry run: would write {profile_file}[/dim]")
        else:
            profile_file.write_text(profile_content, encoding="utf-8")

        # Format match dir pattern (e.g. ~/code/personal/)
        match_dir = ident.match_directory.rstrip("/\\") + "/"
        include_block = f'[includeIf "gitdir:{match_dir}"]\n\tpath = {profile_file}\n'

        if include_block not in updated_content:
            if not updated_content.endswith("\n"):
                updated_content += "\n"
            updated_content += f"\n# Managed identity: {ident.name} ({ident.id})\n{include_block}"
            if dry_run:
                console.print(f"[dim]Dry run: would route gitdir:{match_dir} to {profile_file} in ~/.gitconfig[/dim]")

    if updated_content != existing_content and not dry_run:
        gitconfig_path.write_text(updated_content, encoding="utf-8")
        console.print("[bold green]✓ Configured conditional Git directory routing in ~/.gitconfig[/bold green]")


def run_git_setup(ctx: StepContext) -> None:
    """Set up Git configuration and conditional multi-identity profiles."""
    current_name = get_git_config_val("user.name")
    current_email = get_git_config_val("user.email")

    if current_name and current_email:
        console.print(f"[green]✓ Current Global Git identity:[/green] {current_name} <{current_email}>")
    else:
        if ctx.mode in ("express", "agent"):
            console.print(
                "[yellow]! Git user.name or user.email not set. Please configure manually or run in full mode.[/yellow]"
            )
            return

        name = Prompt.ask("Enter your Git display name", default=current_name or "")
        email = Prompt.ask("Enter your Git email address", default=current_email or "")

        if not ctx.dry_run and name and email:
            set_git_config_val("user.name", name)
            set_git_config_val("user.email", email)
            console.print(f"[bold green]✓ Configured global Git user: {name} <{email}>[/bold green]")

    # Sensible modern defaults
    if not ctx.dry_run:
        set_git_config_val("init.defaultBranch", "main")
        set_git_config_val("pull.rebase", "false")
        set_git_config_val("push.autoSetupRemote", "true")
        set_git_config_val("core.autocrlf", "input" if not ctx.is_windows else "true")

    # Apply conditional directory includes from config
    cfg = load_user_config()
    if cfg.identities:
        apply_conditional_git_includes(cfg.identities, dry_run=ctx.dry_run)
    elif ctx.mode == "full":
        prompt_msg = "Would you like to configure directory-specific Git identities (e.g. personal vs work)?"
        if Confirm.ask(prompt_msg, default=False):
            ident_id = Prompt.ask("Identifier (e.g. personal, work)", default="personal")
            ident_name = Prompt.ask("Display Name", default=current_name or "")
            ident_email = Prompt.ask("Email Address", default=current_email or "")
            match_dir = Prompt.ask("Directory pattern to match (e.g. ~/code/personal/)", default="~/code/personal/")

            new_ident = GitIdentity(id=ident_id, name=ident_name, email=ident_email, match_directory=match_dir)
            cfg.identities.append(new_ident)
            if ctx.dry_run:
                console.print("[dim]Dry run: would save the identity to the devenv config[/dim]")
            else:
                save_user_config(cfg)
            apply_conditional_git_includes([new_ident], dry_run=ctx.dry_run)

    # SSH key verification
    ssh_dir = Path.home() / ".ssh"
    id_ed25519 = ssh_dir / "id_ed25519"
    id_rsa = ssh_dir / "id_rsa"

    if id_ed25519.exists() or id_rsa.exists():
        console.print("[green]✓ SSH key found in ~/.ssh[/green]")
    else:
        console.print("[yellow]! No standard SSH key found in ~/.ssh[/yellow]")
        if ctx.dry_run:
            console.print(f"[dim]Dry run: would offer to generate an ed25519 key at {id_ed25519}[/dim]")
        elif ctx.mode == "full" and Confirm.ask("Would you like to generate a new ed25519 SSH key?", default=True):
            email = get_git_config_val("user.email") or "user@localhost"
            ssh_dir.mkdir(parents=True, exist_ok=True)
            key_path = str(id_ed25519)
            cmd = ["ssh-keygen", "-t", "ed25519", "-C", email, "-f", key_path, "-N", ""]
            res = subprocess.run(cmd, capture_output=True, text=True)
            if res.returncode == 0:
                console.print(f"[bold green]✓ Generated new SSH key at {key_path}[/bold green]")
                pub_key = (id_ed25519.with_suffix(".pub")).read_text(encoding="utf-8").strip()
                console.print(
                    f"\n[cyan]Public Key (add to GitHub -> Settings -> SSH Keys):[/cyan]\n[bold]{pub_key}[/bold]\n"
                )
            else:
                console.print(f"[red]Failed to generate SSH key: {res.stderr}[/red]")
