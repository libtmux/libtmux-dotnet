"""The portable shape of an MCP server entry.

Every CLI stores the same three things — a command, its arguments and its
environment — and then disagrees about how to write them down. This holds the
shared shape and each dialect's rendering of it, so the config editors and the
builders can pass one value between them without either owning it.
"""

from __future__ import annotations

import dataclasses
import pathlib
import typing as t

#: How one CLI wants an entry written. The same file format does not imply
#: the same entry shape.
Dialect = t.Literal["standard", "claude", "opencode"]


@dataclasses.dataclass
class McpServerSpec:
    """The portable shape shared across CLI configs."""

    command: str
    args: list[str] = dataclasses.field(default_factory=list)
    env: dict[str, str] = dataclasses.field(default_factory=dict)

    def to_entry_dict(self, dialect: Dialect = "standard") -> dict[str, t.Any]:
        """Serialize to the entry shape ``dialect`` expects."""
        # Claude's format always includes ``type`` and ``env`` (even when
        # empty); the standard shape omits both when there is nothing to say.
        if dialect == "claude":
            return {
                "type": "stdio",
                "command": self.command,
                "args": list(self.args),
                "env": dict(self.env),
            }
        if dialect == "opencode":
            # One array for argv, and the table is "environment" -- an
            # "env" key here is dropped in silence, and a scalar command
            # is a decode error that takes the whole config down with it.
            local: dict[str, t.Any] = {
                "type": "local",
                "command": [self.command, *self.args],
            }
            if self.env:
                local["environment"] = dict(self.env)
            return local
        out: dict[str, t.Any] = {"command": self.command, "args": list(self.args)}
        if self.env:
            out["env"] = dict(self.env)
        return out

    def project_path(self) -> pathlib.Path | None:
        """Extract ``--project`` from a ``dotnet run`` spec, if any."""
        if pathlib.Path(self.command).name not in {"dotnet", "dotnet.exe"}:
            return None
        try:
            i = self.args.index("--project")
        except ValueError:
            return None
        if i + 1 >= len(self.args):
            return None
        return pathlib.Path(self.args[i + 1])

    def built_binary_path(self) -> pathlib.Path | None:
        """Return the binary this spec launches directly, if it launches one.

        A configuration build is invoked by absolute path rather than
        through ``dotnet``, so the agent starts the server without a build
        step in front of it. That is the shape this recognises.
        """
        if self.project_path() is not None or "/" not in self.command:
            return None
        return pathlib.Path(self.command)

    def _bin_parts(self) -> tuple[pathlib.Path, str] | None:
        """Split a built path into its project directory and configuration.

        The layout is ``<project>/bin/<Configuration>/<framework>/<binary>``,
        so the configuration is three levels up from the binary.
        """
        binary = self.built_binary_path()
        if binary is None:
            return None
        framework_dir = binary.parent
        configuration_dir = framework_dir.parent
        if configuration_dir.parent.name != "bin":
            return None
        return configuration_dir.parent.parent, configuration_dir.name

    def local_repo_path(self) -> pathlib.Path | None:
        """Return the repo a spec points into, whichever shape it uses."""
        project = self.project_path()
        if project is not None:
            # src/<Name>/<Name>.csproj -> repo root
            return project.parent.parent.parent
        parts = self._bin_parts()
        if parts is not None:
            # src/<Name> -> repo root
            return parts[0].parent.parent
        return None

    def dotnet_configuration(self) -> str | None:
        """Return ``Debug`` or ``Release`` for a configuration build."""
        parts = self._bin_parts()
        return None if parts is None else parts[1]
