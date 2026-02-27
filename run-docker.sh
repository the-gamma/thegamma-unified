#!/bin/bash
docker run -p 5000:8080 \
  -e THEGAMMA_BASE_URL=http://localhost:5000 \
  -v /c/Tomas/Public/the-gamma/thegamma-unified/storage:/app/storage \
  tomasp/thegamma-unified:0.0.3
