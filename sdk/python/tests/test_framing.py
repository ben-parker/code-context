import json
import pathlib
import unittest

from codecontext_worker_sdk import (
    BASELINE_ACCEPTED,
    BASELINE_REJECTED,
    CAPABILITIES,
    METHODS,
    MessageDecoder,
    WorkerInputs,
    encode_message,
)

SCHEMA = pathlib.Path(__file__).resolve().parents[3] / "src" / "CodeContext.Parser.Protocol" / "protocol" / "parser-protocol.schema.json"


class FramingTests(unittest.TestCase):
    def test_exports_analysis_progress_notification_method(self) -> None:
        self.assertEqual("analysis/progress", METHODS["analysis_progress"])

    def test_exports_the_persistent_state_method_capability_and_baseline_answers(self) -> None:
        self.assertEqual("workspace/saveState", METHODS["save_state"])
        self.assertEqual("persistentState", CAPABILITIES["persistent_state"])
        self.assertEqual(("accepted", "rejected"), (BASELINE_ACCEPTED, BASELINE_REJECTED))

    def test_worker_inputs_name_the_schema_fields(self) -> None:
        schema = json.loads(SCHEMA.read_text(encoding="utf-8"))["$defs"]
        self.assertEqual(set(schema["workerInputs"]["properties"]), set(WorkerInputs.__annotations__))
        self.assertEqual("#/$defs/workerInputs", schema["analysisDelta"]["properties"]["inputs"]["$ref"])

    def test_fragmented_and_adjacent_frames(self) -> None:
        first = encode_message({"jsonrpc": "2.0", "id": 1, "result": "héllo"})
        second = encode_message({"jsonrpc": "2.0", "method": "shutdown"})
        decoder = MessageDecoder()

        self.assertEqual([], decoder.push(first[:7]))
        self.assertEqual(
            [
                {"jsonrpc": "2.0", "id": 1, "result": "héllo"},
                {"jsonrpc": "2.0", "method": "shutdown"},
            ],
            decoder.push(first[7:] + second),
        )


if __name__ == "__main__":
    unittest.main()
