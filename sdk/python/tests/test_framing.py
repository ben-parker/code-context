import unittest

from codecontext_worker_sdk import MessageDecoder, encode_message


class FramingTests(unittest.TestCase):
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
