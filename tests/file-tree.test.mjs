// 공유 파일 계층뷰의 순수 트리 생성 로직을 검사한다.
// app.js는 IIFE이므로 fileTreeRows만 떼어내 브라우저와 같은 입력으로 실행한다.
import { readFileSync } from "node:fs";
import assert from "node:assert/strict";
import test from "node:test";

const appSource = readFileSync(
  new URL("../host/ClassroomLive.Host/wwwroot/app.js", import.meta.url),
  "utf8",
);
const treeBlock = appSource.match(
  /function fileTreeRows\(project\) \{[\s\S]*?\r?\n  \}(?=\r?\n\r?\n  function createFolderItem)/,
);
assert.ok(treeBlock, "app.js에서 fileTreeRows 함수를 찾지 못했습니다.");

const fileTreeRows = (project, collapsed = []) => new Function(
  "collapsedFolders",
  `${treeBlock[0]}\nreturn fileTreeRows;`,
)(new Set(collapsed))(project);

const file = (id, path) => ({
  id,
  path,
  name: path.replaceAll("\\", "/").split("/").at(-1),
});
const project = (files, values = {}) => ({
  id: "project-1",
  name: "Example",
  loose: true,
  files,
  ...values,
});
const describe = (rows) => rows.map((row) => row.type === "folder"
  ? `folder:${row.name}:${row.depth}:${row.key}`
  : `file:${row.file.name}:${row.depth}`);

test("루트 파일과 폴더 파일을 안정적으로 정렬한다", () => {
  const rows = fileTreeRows(project([
    file("3", "src/Z.cs"),
    file("2", "A.cs"),
    file("1", "src/B.cs"),
  ]));
  assert.deepEqual(describe(rows), [
    "file:A.cs:0",
    "folder:src:0:project-1:src",
    "file:B.cs:1",
    "file:Z.cs:1",
  ]);
});

test("Windows 경로의 대소문자가 달라도 같은 폴더로 합친다", () => {
  const rows = fileTreeRows(project([
    file("1", "Src\\One.cs"),
    file("2", "src/Two.cs"),
  ]));
  assert.deepEqual(describe(rows), [
    "folder:Src:0:project-1:src",
    "file:One.cs:1",
    "file:Two.cs:1",
  ]);
});

test("공유 파일 없이 하나로만 이어지는 중간 폴더를 압축한다", () => {
  const rows = fileTreeRows(project([
    file("1", "outer/inner/One.cs"),
    file("2", "outer/inner/Two.cs"),
  ]));
  assert.deepEqual(describe(rows), [
    "folder:inner:0:project-1:outer/inner",
    "file:One.cs:1",
    "file:Two.cs:1",
  ]);
});

test("분기되는 폴더는 생략하지 않고 계층을 유지한다", () => {
  const rows = fileTreeRows(project([
    file("1", "src/alpha/A.cs"),
    file("2", "src/beta/B.cs"),
  ]));
  assert.deepEqual(describe(rows), [
    "folder:src:0:project-1:src",
    "folder:alpha:1:project-1:src/alpha",
    "file:A.cs:2",
    "folder:beta:1:project-1:src/beta",
    "file:B.cs:2",
  ]);
});

test("접힌 폴더의 모든 자손을 행 목록에서 제외한다", () => {
  const source = project([
    file("1", "src/alpha/A.cs"),
    file("2", "src/beta/B.cs"),
  ]);
  const rows = fileTreeRows(source, ["project-1:src"]);
  assert.deepEqual(describe(rows), ["folder:src:0:project-1:src"]);
});

test("프로젝트 제목과 같은 프로젝트 루트 폴더는 중복 표시하지 않는다", () => {
  const rows = fileTreeRows(project([
    file("1", "src/example/Features/A.cs"),
    file("2", "src/EXAMPLE/B.cs"),
  ], { loose: false }));
  assert.deepEqual(describe(rows), [
    "file:B.cs:0",
    "folder:Features:0:project-1:features",
    "file:A.cs:1",
  ]);
});

test("F2로 프로젝트 표시 이름을 바꿔도 실제 프로젝트 루트는 중복 표시하지 않는다", () => {
  const rows = fileTreeRows(project([
    file("1", "LearnC_Control/Main.C"),
    file("2", "LearnC_Control/include/Helper.h"),
  ], { name: "5.LearnC_Control(Visual Studio 2022)", root: "LearnC_Control", loose: false }));
  assert.deepEqual(describe(rows), [
    "file:Main.C:0",
    "folder:include:0:project-1:include",
    "file:Helper.h:1",
  ]);
});

test("실제 프로젝트 루트는 경로의 정확한 접두사일 때만 제거한다", () => {
  const rows = fileTreeRows(project([
    file("1", "LearnC_Control_Old/Main.C"),
  ], { name: "Renamed", root: "LearnC_Control", loose: false }));
  assert.deepEqual(describe(rows), [
    "folder:LearnC_Control_Old:0:project-1:learnc_control_old",
    "file:Main.C:1",
  ]);
});

test("프로젝트 파일이 솔루션 루트에 있으면 이름 기반 추정을 사용하지 않는다", () => {
  const rows = fileTreeRows(project([
    file("1", "Example/Main.C"),
  ], { name: "Example", root: ".", loose: false }));
  assert.deepEqual(describe(rows), [
    "folder:Example:0:project-1:example",
    "file:Main.C:1",
  ]);
});

test("이름이 같은 폴더도 부모 경로가 다르면 접기 키가 충돌하지 않는다", () => {
  const rows = fileTreeRows(project([
    file("1", "first/common/A.cs"),
    file("2", "second/common/B.cs"),
  ]));
  assert.deepEqual(
    rows.filter((row) => row.type === "folder" && row.name === "common").map((row) => row.key),
    ["project-1:first/common", "project-1:second/common"],
  );
});
