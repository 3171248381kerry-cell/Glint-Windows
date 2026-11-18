"""Glint's bundled worker. Model plugins contain data only; stdout is private JSON IPC."""
import hashlib
import json
import os
from pathlib import Path
import sys

sys.stdin.reconfigure(encoding="utf-8")
sys.stdout.reconfigure(encoding="utf-8", line_buffering=True)
sys.stderr.reconfigure(encoding="utf-8")
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"

_tagger = None
_tagger_failed = False


def respond(value):
    print(json.dumps(value, ensure_ascii=False), flush=True)


def has_kanji(text):
    return any("\u3400" <= ch <= "\u4dbf" or "\u4e00" <= ch <= "\u9fff" for ch in text)


def to_hiragana(text):
    chars = []
    for ch in text:
        code = ord(ch)
        chars.append(chr(code - 0x60) if 0x30A1 <= code <= 0x30F6 else ch)
    return "".join(chars)


def reading_of(text):
    global _tagger, _tagger_failed
    if not has_kanji(text) or _tagger_failed:
        return None
    if _tagger is None:
        try:
            import fugashi
            _tagger = fugashi.Tagger()
        except Exception:
            _tagger_failed = True
            return None
    parts = []
    try:
        for word in _tagger(text):
            kana = getattr(word.feature, "kana", None) or ""
            if not kana:
                kana = word.surface
            parts.append(to_hiragana(str(kana)))
    except Exception:
        return None
    value = "".join(parts)
    if has_kanji(value) or not any("\u3041" <= ch <= "\u3096" for ch in value):
        return None
    return value


def load_model(model, lock_path):
    import ctranslate2
    import sentencepiece

    lock = json.loads(lock_path.read_text(encoding="utf-8-sig"))
    for relative, expected in lock["files"].items():
        path = model / Path(relative).relative_to("model")
        if path.is_symlink() or not path.is_file():
            raise ValueError("model integrity")
        with path.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if path.stat().st_size != expected["sizeBytes"] or digest != expected["sha256"]:
            raise ValueError("model integrity")
    source = sentencepiece.SentencePieceProcessor(model_file=str(model / "source.spm"))
    target = sentencepiece.SentencePieceProcessor(model_file=str(model / "target.spm"))
    translator = ctranslate2.Translator(str(model), device="cpu", compute_type="int8", inter_threads=1, intra_threads=min(4, os.cpu_count() or 1))
    return translator, source, target


def main():
    root = Path(sys.argv[1]).resolve()
    app = Path(sys.argv[2]).resolve()
    target = sys.argv[3]
    specs = {
        "en": (root / "mt-opus-zh-en" / "model", app / "Data" / "model-lock.json"),
        "ja": (root / "mt-opus-zh-ja" / "model", app / "Runtime" / "locks" / "ja.json"),
        "ko": (root / "mt-opus-zh-ko" / "model", app / "Runtime" / "locks" / "ko.json"),
    }
    model, lock_path = specs[target]
    translator, source, decoder = load_model(model, lock_path)
    respond({"ready": True})
    for line in sys.stdin:
        request = None
        try:
            if len(line) > 4096:
                raise ValueError("oversize request")
            request = json.loads(line)
            text = request["text"]
            if not isinstance(text, str) or not 0 < len(text) <= 200 or request.get("target", "en") != target:
                raise ValueError("invalid request")
            tokens = source.encode(text, out_type=str) + ["</s>"]
            result = translator.translate_batch([tokens], beam_size=4, max_decoding_length=128, max_input_length=256, repetition_penalty=1.05)[0]
            pieces = [piece for piece in result.hypotheses[0] if piece not in {"</s>", "<pad>"}]
            output = decoder.decode(pieces).strip()
            payload = {"id": request["id"], "text": output}
            if target == "ja":
                reading = reading_of(output)
                if reading:
                    payload["reading"] = reading
            respond(payload)
        except Exception:
            # Never echo input text or exception strings, which can contain user input.
            respond({"id": request.get("id", -1) if isinstance(request, dict) else -1, "error": "translation_failed"})


if __name__ == "__main__":
    try:
        main()
    except Exception:
        respond({"ready": False, "error": "runtime_unavailable"})
        sys.exit(1)
