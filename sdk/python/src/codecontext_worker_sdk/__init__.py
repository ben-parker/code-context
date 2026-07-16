"""Small, dependency-free helpers for CodeContext language workers."""

from __future__ import annotations

import json
from typing import Any

PROTOCOL_VERSION = 1
METHODS = {
    "initialize": "initialize",
    "open_workspace": "workspace/open",
    "index_workspace": "workspace/index",
    "apply_changes": "workspace/applyChanges",
    "get_native_syntax_tree": "syntaxTree/get",
    "cancel": "$/cancel",
    "analysis_delta": "analysis/delta",
    "shutdown": "shutdown",
}


def encode_message(message: Any) -> bytes:
    payload = json.dumps(message, ensure_ascii=False, separators=(",", ":")).encode("utf-8")
    return f"Content-Length: {len(payload)}\r\n\r\n".encode("ascii") + payload


class MessageDecoder:
    def __init__(self, max_content_length: int = 16 * 1024 * 1024) -> None:
        self._buffer = bytearray()
        self._max_content_length = max_content_length

    def push(self, chunk: bytes | bytearray | memoryview) -> list[Any]:
        self._buffer.extend(chunk)
        messages: list[Any] = []
        while True:
            header_end = self._buffer.find(b"\r\n\r\n")
            if header_end < 0:
                return messages
            headers = bytes(self._buffer[:header_end]).decode("ascii")
            length: int | None = None
            for header in headers.split("\r\n"):
                name, separator, value = header.partition(":")
                if separator and name.lower() == "content-length":
                    length = int(value.strip())
                    break
            if length is None:
                raise ValueError("Frame is missing Content-Length.")
            if length < 0 or length > self._max_content_length:
                raise ValueError(f"Invalid Content-Length: {length}.")
            frame_end = header_end + 4 + length
            if len(self._buffer) < frame_end:
                return messages
            payload = bytes(self._buffer[header_end + 4:frame_end])
            del self._buffer[:frame_end]
            messages.append(json.loads(payload.decode("utf-8")))
