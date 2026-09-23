# Laya in XivMcp

XivMcp can expose a local Laya typed-decision model as an ordinary MCP tool.

## Tool

`laya_query` accepts:

- `state`: text/context to classify.
- `questions`: a JSON object using Laya/JEV question definitions.
- `endpoint`: optional; defaults to `http://127.0.0.1:8080/v1/systemone`.

Example MCP arguments:

```json
{
  "state": "I am level 90 and deciding whether this inventory item matches the stated condition.",
  "questions": "{\"matches\":{\"type\":\"noul\",\"instructions\":\"Does the state satisfy the condition?\"}}"
}
```

The bridge is intentionally read-only. A Laya answer is returned with
`advisory: true`; it cannot bypass XivMcp's Action/Chat permission tiers or
the in-game approval gate.

## Runtime placement

Run Laya on the same Fedora host as FFXIV/XIVLauncher. XivMcp runs under Wine,
whose loopback reaches the Linux host in the supported setup. The endpoint is
restricted to loopback HTTP so an MCP caller cannot turn this tool into an
arbitrary HTTP proxy.

The companion Atlas installer currently lives in `Spaceghost/atlas-infra` as
`scripts/install-laya-fedora`. It builds laya.cpp for RTX 3070 and RTX 4060
and installs the loopback systemd service.
