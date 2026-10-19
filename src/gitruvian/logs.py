"""Log técnico local. Registra apenas metadados (ids, contagens, códigos de saída).

Nunca passe conteúdo de arquivos, diffs, prompts ou respostas do modelo para o logger.
Como defesa extra, todo registro passa por redação de segredos e é truncado.
"""

from __future__ import annotations

import logging
from logging.handlers import RotatingFileHandler
from pathlib import Path

from gitruvian.safety import redact, truncate

LOGGER_NAME = "gitruvian"
MAX_MESSAGE_CHARS = 300


class SafeFilter(logging.Filter):
    def filter(self, record: logging.LogRecord) -> bool:
        message = record.getMessage()
        record.msg = truncate(redact(message.replace("\n", " ")), MAX_MESSAGE_CHARS)
        record.args = ()
        return True


def setup_logging(data_dir: Path) -> logging.Logger:
    logger = logging.getLogger(LOGGER_NAME)
    logger.setLevel(logging.INFO)
    log_path = data_dir / "gitruvian.log"
    for handler in list(logger.handlers):
        if getattr(handler, "baseFilename", None) == str(log_path):
            return logger
        logger.removeHandler(handler)
        handler.close()
    handler = RotatingFileHandler(log_path, maxBytes=512_000, backupCount=2, encoding="utf-8")
    handler.setFormatter(logging.Formatter("%(asctime)s %(levelname)s %(message)s"))
    handler.addFilter(SafeFilter())
    logger.addHandler(handler)
    logger.propagate = False
    return logger


def get_logger() -> logging.Logger:
    return logging.getLogger(LOGGER_NAME)
