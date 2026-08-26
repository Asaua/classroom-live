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
const edgeScrollBlock = appSource.match(
  /function touchEdgeScrollDelta\(position, start, end\) \{[\s\S]*?\r?\n  \}/,
);
assert.ok(edgeScrollBlock, "app.js에서 touchEdgeScrollDelta 함수를 찾지 못했습니다.");
const touchEdgeScrollDelta =
  new Function(`${edgeScrollBlock[0]}\nreturn touchEdgeScrollDelta;`)();
const dragScrollBlock = appSource.match(
  /function touchDragScrollDelta\(previous, current\) \{[\s\S]*?\r?\n  \}/,
);
assert.ok(dragScrollBlock, "app.js에서 touchDragScrollDelta 함수를 찾지 못했습니다.");
const touchDragScrollDelta =
  new Function(`${dragScrollBlock[0]}\nreturn touchDragScrollDelta;`)();
const pointerEndBlock = appSource.match(
  /function touchPointerEndAction\(pointerId, primaryPointer, scrollPointer\) \{[\s\S]*?\r?\n  \}/,
);
assert.ok(pointerEndBlock, "app.js에서 touchPointerEndAction 함수를 찾지 못했습니다.");
const touchPointerEndAction =
  new Function(`${pointerEndBlock[0]}\nreturn touchPointerEndAction;`)();

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

test("터치 선택은 코드박스 가장자리에서만 자동 스크롤한다", () => {
  assert.equal(touchEdgeScrollDelta(200, 100, 300), 0);
  assert.ok(touchEdgeScrollDelta(110, 100, 300) < 0);
  assert.ok(touchEdgeScrollDelta(290, 100, 300) > 0);
  assert.equal(touchEdgeScrollDelta(100, 100, 300), -16);
  assert.equal(touchEdgeScrollDelta(300, 100, 300), 16);
});

test("두 번째 손가락 드래그를 코드박스 스크롤 방향으로 변환한다", () => {
  assert.equal(touchDragScrollDelta(100, 80), 20);
  assert.equal(touchDragScrollDelta(100, 125), -25);
  assert.equal(touchDragScrollDelta(100, 100), 0);
});

test("두 번째 손가락을 떼어도 첫 손가락의 선택 제스처는 유지한다", () => {
  assert.equal(touchPointerEndAction(22, 11, 22), "release-scroll");
  assert.equal(touchPointerEndAction(11, 11, 22), "finish");
  assert.equal(touchPointerEndAction(33, 11, 22), "ignore");
});
