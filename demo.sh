#!/usr/bin/env bash
# Proves: an MCP tools/list + tools/call surface backed by a real Workflow Core host.
#   bash demo.sh          all checks must PASS, exit 0
#   bash demo.sh --break  run against a sabotaged catalog, a check must FAIL, exit 1
set -euo pipefail
cd "$(dirname "$0")"
[[ -d "$HOME/.dotnet" ]] && export PATH="$HOME/.dotnet:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

MODE="${1:-}"
OUT=.demo
DLL=samples/OrderTools/bin/Release/net8.0/OrderTools.dll
rm -rf "$OUT" && mkdir -p "$OUT"

dotnet build samples/OrderTools -c Release -v quiet -nologo > "$OUT/build.log" 2>&1 || { cat "$OUT/build.log"; exit 1; }

CATALOG=catalog.json
if [[ "$MODE" == "--break" ]]; then
  # Sabotage the mapping: same tool name, wrong workflow definition.
  jq '.tools[0].workflowId = "CancelOrder"' catalog.json > "$OUT/catalog.broken.json"
  CATALOG="$OUT/catalog.broken.json"
  echo "BREAK MODE: place_order now maps to CancelOrder. Expect a FAIL."
elif [[ -n "$MODE" ]]; then
  echo "usage: bash demo.sh [--break]"; exit 2
fi

REQUESTS='{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18"}}
{"jsonrpc":"2.0","method":"notifications/initialized"}
{"jsonrpc":"2.0","id":2,"method":"tools/list"}
{"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"place_order","arguments":{"orderId":"A-1001","quantity":3,"unitPrice":4.5}}}'

# Same requests, two servers: one with the catalog, one with nothing registered.
echo "$REQUESTS" | dotnet "$DLL" serve --catalog "$CATALOG" > "$OUT/registered.jsonl" 2> "$OUT/registered.err"
echo "$REQUESTS" | dotnet "$DLL" serve                       > "$OUT/empty.jsonl"      2> "$OUT/empty.err"

FAILED=0
check() {  # check <label> <jq filter that must print true> <file>
  if [[ "$(jq -s "$2" "$3")" == "true" ]]; then echo "CHECK PASS  $1"; else echo "CHECK FAIL  $1"; FAILED=1; fi
}
instances() { sed -n 's/^wfmcp: instances_total=//p' "$1"; }

R="$OUT/registered.jsonl"; E="$OUT/empty.jsonl"
CALL='.[] | select(.id==3) | .result.structuredContent'

check "1 registered: tools/list advertises place_order" \
  '[.[] | select(.id==2) | .result.tools[].name] == ["place_order"]' "$R"
check "2 registered: tools/call started a real instance (non-empty instanceId, ran PlaceOrder v1)" \
  "$CALL | (.instanceId | length > 0) and .workflowId == \"PlaceOrder\" and .version == 1" "$R"
check "3 registered: instance reached Complete with expected output" \
  "$CALL | .ok == true and .status == \"Complete\" and .output.total == 13.5 and .output.confirmation == \"CONF-A-1001\" and .output.trail == [\"validate\",\"price\",\"waited\",\"confirm\"]" "$R"
n=$(instances "$OUT/registered.err")
if [[ "$n" == "1" ]]; then echo "CHECK PASS  4 registered: Workflow Core store holds exactly 1 instance"; else echo "CHECK FAIL  4 registered: store holds '${n}' instances, want 1"; FAILED=1; fi

check "5 empty catalog: tools/list is empty" \
  '[.[] | select(.id==2) | .result.tools | length] == [0]' "$E"
check "6 empty catalog: tools/call returns tool_not_found" \
  '[.[] | select(.id==3) | .error.data.error] == ["tool_not_found"]' "$E"
n=$(instances "$OUT/empty.err")
if [[ "$n" == "0" ]]; then echo "CHECK PASS  7 empty catalog: Workflow Core store holds 0 instances"; else echo "CHECK FAIL  7 empty catalog: store holds '${n}' instances, want 0"; FAILED=1; fi

echo "--- tools/call structuredContent (registered) ---"
jq -c "$CALL" -s "$R"

if [[ $FAILED -ne 0 ]]; then echo "RESULT: FAIL (raw output in $OUT/)"; exit 1; fi
echo "RESULT: PASS"
