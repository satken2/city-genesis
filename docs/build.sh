#!/bin/bash
if [ -L CLAUDE.md ]; then
  target=$(readlink CLAUDE.md)
  rm CLAUDE.md
  cp "$target" CLAUDE.md
fi
mkdocs build
