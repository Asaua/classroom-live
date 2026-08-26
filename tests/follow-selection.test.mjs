import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import test from "node:test";

const appSource = readFileSync(
  new URL("../host/ClassroomLive.Host/wwwroot/app.js", import.meta.url),
  "utf8",
);
const functionBlock = appSource.match(
  /function lineSelectionRange\(anchor, line, extend\) \{[\s\S]*?\r?\n  \}/,
);
assert.ok(functionBlock, "app.js에서 lineSelectionRange 함수를 찾지 못했습니다.");
const lineSelectionRange = new Function(`${functionBlock[0]}\nreturn lineSelectionRange;`)();
const viewportBlock = appSource.match(
  /function overlapsViewport\(top, bottom, viewTop, viewBottom\) \{[\s\S]*?\r?\n  \}/,
);
assert.ok(viewportBlock, "app.js에서 overlapsViewport 함수를 찾지 못했습니다.");
const overlapsViewport = new Function(`${viewportBlock[0]}\nreturn overlapsViewport;`)();

test("줄 클릭과 Shift 범위 선택의 기준점을 유지한다", () => {
  assert.deepEqual(lineSelectionRange(0, 7, false), { anchor: 7, start: 7, end: 7 });
  assert.deepEqual(lineSelectionRange(7, 11, true), { anchor: 7, start: 7, end: 11 });
  assert.deepEqual(lineSelectionRange(7, 3, true), { anchor: 7, start: 3, end: 7 });
  assert.deepEqual(lineSelectionRange(7, 9, false), { anchor: 9, start: 9, end: 9 });
});

test("교수 줄이 화면에 한 픽셀이라도 보이면 따라가기를 유지한다", () => {
  assert.equal(overlapsViewport(10, 20, 19, 30), true);
  assert.equal(overlapsViewport(20, 30, 10, 21), true);
  assert.equal(overlapsViewport(10, 20, 20, 30), false);
  assert.equal(overlapsViewport(30, 40, 10, 30), false);
});
