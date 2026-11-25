# parti-mcp

MCP server bridging external agents (Claude Desktop, Cursor, OpenClaw, etc.) to the Parti Grasshopper plugin over TCP.

Status: **placeholder**. Real bridge lands in Phase 2.

## Reproducible development install

Use Python 3.10 or newer in a virtual environment, from this directory:

```bash
python -m pip install --require-hashes --only-binary=:all: -r requirements.lock
python -m pip install --no-deps --no-build-isolation -e .
python -m pip check
```

`requirements.lock` covers runtime dependencies and the build backend with exact
versions and SHA-256 hashes. Install the local project without dependency resolution
or build isolation so pip cannot silently resolve a different backend. CI uses the
same commands. No live bridge is started by the import check.

To deliberately refresh the lock, keep `requirements-build.in` and
`build-system.requires` in sync, then run from the repository root with `uv`:

```bash
uv pip compile mcp/parti_mcp/pyproject.toml mcp/parti_mcp/requirements-build.in --universal --python-version 3.10 --generate-hashes --no-annotate --no-header -o mcp/parti_mcp/requirements.lock
```

Review the resulting versions and hashes before committing the refreshed lock.
