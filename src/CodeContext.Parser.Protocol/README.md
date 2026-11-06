# CodeContext worker protocol (.NET)

This package provides the versioned parser-worker contracts, Content-Length framing,
JSON-RPC connection, manifest model, and source-generated serializers used by
CodeContext. The [canonical cross-language schema](protocol/parser-protocol.schema.json)
is packaged under `protocol/parser-protocol.schema.json`.

See the [language-worker SDK guide](../../docs/language-worker-sdk.md) for the worker
lifecycle, ownership rules, discovery precedence, native-tree guidance, and the optional
`persistentState` capability (`IndexBaseline`, `UnitFingerprint`, `SaveStateParams`,
`SaveStateResult`, `ParserProtocolMethods.SaveState`), which lets a worker save its state and
confirm the host's saved graph on the next start instead of re-walking it. Protocol version
stays 1: every addition is optional. The
[OpenAPI document](../../codecontext.openapi.json) describes the separate host REST
contract, and the [agent skill](../../skill/SKILL.md) explains how clients query it.
