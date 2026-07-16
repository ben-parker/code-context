import sys

def hello_world(name: str) -> str:
    """Simple greeting function."""
    return f"Hello from Python, {name}!"

def get_python_version() -> str:
    """Return the current Python version."""
    return f"Python {sys.version}"


def process_data(data: dict[str, any]) -> dict[str, any]:
    """Process a dictionary and return modified version."""
    result = data.copy()
    result["processed"] = True
    result["message"] = f"Processed {len(data)} items"
    return result


def get_numbers(count: int) -> list[int]:
    """Generate a list of numbers."""
    return list(range(count))


def add_numbers(a: int, b: int = 10) -> int:
    """Add two numbers with default parameter."""
    return a + b