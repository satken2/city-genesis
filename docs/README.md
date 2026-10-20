English | [Japanese (original)](README.ja.md)

# City Genesis documentation

Public documentation for Unity and Tauri OSS developers, readable directly as Markdown on GitHub without a site build. **Japanese is authoritative; English is its translation.** Internal details of the private core remain in the separate `genesis-core/docs/` repository directory.

- [English introduction and contents](en/README.md)
- [Japanese introduction and contents](ja/README.md)
- [Shared OpenAPI specification](api/openapi.json)

## Structure and editing

| Location | Contents |
| --- | --- |
| `ja/` | Japanese sources, indexed by `ja/README.md` |
| `en/` | English translations, indexed by `en/README.md` |
| `{ja,en}/economics/` | Economic concepts and observable game rules |
| `{ja,en}/geometries/` | Shape data and shared rendering mechanisms |
| `{ja,en}/programs/` | Front-end architecture, foundations, and component communication |
| `{ja,en}/contributing/` | Translation terminology and asset attribution |
| `api/` | Shared generated API specification |
| `images/` | Shared images and diagrams |

Edit Japanese first and update its English counterpart in the same change. Japanese takes precedence if meanings differ. When adding, removing, or moving pages, update both language indexes and related links. [AGENTS.md](AGENTS.md) defines writing conventions. Explain each concept once and link to it elsewhere.

## Update the API specification

`fetch_openapi.py` retrieves OpenAPI and updates the shared `api/openapi.json`. Do not edit or translate the generated specification manually. The script uses only Python's standard library. Run from this directory:

```powershell
python fetch_openapi.py
python fetch_openapi.py --source http://localhost:5000/openapi.json
python fetch_openapi.py --source ./my-spec.json
```

Without a source, it tries localhost:5000 and falls back to `../unity/openapi.json`. Use `--source` for another core port. Interactive API documentation is available at `/docs` on the running core.
