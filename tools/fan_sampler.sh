#!/usr/bin/env bash
# fan_sampler.sh -- 实验期间旁路采样 NBFC 占空比
# 为什么需要：tplab 的 CSV 只写 fan_out_pct（实验计划里指定的风扇百分比），
# 计划没指定风扇时该列为空；而 NBFC 自动曲线实际把风扇推到了多少，我们看不到。
# 而 R_th 随风扇变化，没有这条风扇轨迹就解释不了温度平台。
# 注意：NBFC 只回读占空比（0~steps），不是 RPM。
#
# 用法: bash tools/fan_sampler.sh <输出csv> <总秒数> [间隔秒]
set -u
OUT="${1:-fan-sample.csv}"
TOTAL="${2:-1400}"
STEP="${3:-5}"
NBFC="/c/Program Files (x86)/NoteBook FanControl/nbfc.exe"
if [ ! -f "$NBFC" ]; then
  NBFC="$(command -v nbfc.exe 2>/dev/null || command -v nbfc 2>/dev/null || true)"
fi
if [ -z "${NBFC:-}" ] || [ ! -e "$NBFC" ]; then
  echo "nbfc not found" >&2
  exit 1
fi

echo "timestamp,epoch_s,fan_duty,fan_target,auto_control,steps,ec_temp_c" > "$OUT"
i=0
while [ "$i" -lt "$TOTAL" ]; do
  ts="$(date +"%Y-%m-%d %H:%M:%S")"
  ep="$(date +%s)"
  f="$("$NBFC" status -f 2>&1 | tr -d '\r')"
  s="$("$NBFC" status -s 2>&1 | tr -d '\r')"
  duty="$(printf '%s\n' "$f" | grep -i "Current fan speed"  | sed 's/^[^:]*:[[:space:]]*//')"
  tgt="$(printf '%s\n'  "$f" | grep -i "Target fan speed"   | sed 's/^[^:]*:[[:space:]]*//')"
  auto="$(printf '%s\n' "$f" | grep -i "Auto control enabled"| sed 's/^[^:]*:[[:space:]]*//')"
  steps="$(printf '%s\n' "$f" | grep -i "Fan speed steps"   | sed 's/^[^:]*:[[:space:]]*//')"
  et="$(printf '%s\n'   "$s" | grep -i "^Temperature"      | sed 's/^[^:]*:[[:space:]]*//')"
  echo "$ts,$ep,$duty,$tgt,$auto,$steps,$et" >> "$OUT"
  sleep "$STEP"
  i=$((i + STEP))
done
echo "sampler done -> $OUT"
