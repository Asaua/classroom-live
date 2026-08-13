(() => {
  "use strict";

  const $ = (id) => document.getElementById(id);
  const isHost = location.pathname === "/host";
  const params = new URLSearchParams(location.search);
  const tokenFromUrl = params.get("token");
  const pinFromUrl = params.get("pin");
  if (tokenFromUrl) sessionStorage.setItem("classroom-live:admin", tokenFromUrl);
  if (pinFromUrl) sessionStorage.setItem("classroom-live:pin", pinFromUrl);
  if (tokenFromUrl || pinFromUrl) history.replaceState(null, "", location.pathname);

  const adminToken = sessionStorage.getItem("classroom-live:admin") || "";
  let pin = sessionStorage.getItem("classroom-live:pin") || "";
  let selectedId = localStorage.getItem("classroom-live:selected-file") || "";
  let latestHostState = null;
  let requestRunning = false;
  let blockedUntil = 0;
  let rendered = { fileId: "", content: null };

  // crypto.randomUUID는 보안 컨텍스트(HTTPS 또는 localhost)에서만 존재한다.
  // 학생은 http://192.168.x.x 로 접속하므로 그대로 호출하면 여기서 예외가 나고
  // 아래 코드가 통째로 실행되지 않아 빈 화면만 보인다.
  const newViewerId = () =>
    globalThis.crypto?.randomUUID?.() ??
    `${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`;
  const viewerId = localStorage.getItem("classroom-live:viewer") || newViewerId();
  localStorage.setItem("classroom-live:viewer", viewerId);

  if (isHost) $("hostControls").hidden = false;
  if (!isHost && !pin) showGate("");

  $("joinForm").addEventListener("submit", (event) => {
    event.preventDefault();
    pin = $("pinInput").value.trim();
    sessionStorage.setItem("classroom-live:pin", pin);
    $("gateError").textContent = "";
    void refresh();
  });

  $("mobileFiles").addEventListener("click", () => openFiles(true));
  $("backdrop").addEventListener("click", () => openFiles(false));
  $("toggleBroadcast").addEventListener("click", async () => {
    if (!latestHostState) return;
    await fetch("/api/host/broadcast", {
      method: "POST",
      headers: { "Content-Type": "application/json", "X-Admin-Token": adminToken },
      body: JSON.stringify({ enabled: !latestHostState.broadcasting }),
    });
    await refresh();
  });
  $("copyLink").addEventListener("click", async () => {
    const url = latestHostState?.studentUrls?.[0];
    if (!url) return;
    await navigator.clipboard.writeText(url);
    $("copyLink").textContent = "복사됨";
    setTimeout(() => { $("copyLink").textContent = "학생 주소 복사"; }, 1200);
  });
  $("allowFirewall").addEventListener("click", async () => {
    const button = $("allowFirewall");
    button.disabled = true;
    button.textContent = "권한 요청 중";
    try {
      const response = await fetch("/api/host/firewall", {
        method: "POST", headers: { "X-Admin-Token": adminToken },
      });
      button.textContent = response.ok ? "방화벽 허용됨" : "다시 시도";
    } catch {
      button.textContent = "다시 시도";
    } finally {
      button.disabled = false;
    }
  });

  function openFiles(open) {
    $("filePanel").classList.toggle("is-open", open);
    $("backdrop").hidden = !open;
  }

  function showGate(message) {
    $("gate").hidden = false;
    $("gateError").textContent = message;
  }

  function setText(element, value) {
    if (element.textContent !== value) element.textContent = value;
  }

  async function refresh() {
    if (requestRunning) return;
    // 틀린 PIN을 들고 0.75초마다 계속 두드리면 서버의 시도 제한에 스스로 걸린다.
    if (!isHost && !pin) return;
    if (Date.now() < blockedUntil) return;

    requestRunning = true;
    try {
      const response = await fetch(isHost ? "/api/host/state" : "/api/state", {
        cache: "no-store",
        headers: isHost
          ? { "X-Admin-Token": adminToken }
          : { "X-Classroom-Pin": pin, "X-Viewer-Id": viewerId },
      });

      if (!response.ok) {
        if (!isHost && response.status === 429) {
          blockedUntil = Date.now() + 60_000;
          showGate("PIN 시도가 너무 많습니다. 1분 뒤에 다시 시도해주세요.");
        } else if (!isHost && response.status === 401) {
          pin = "";
          sessionStorage.removeItem("classroom-live:pin");
          showGate("PIN이 맞지 않습니다.");
        }
        setConnection("연결 대기", "waiting");
        return;
      }

      const payload = await response.json();
      latestHostState = isHost ? payload : null;
      const classroom = isHost ? payload.classroom : payload;
      $("gate").hidden = true;
      render(classroom, payload);
    } catch {
      setConnection("재연결 중", "waiting");
      setText($("syncStatus"), "서버 연결 대기 중");
    } finally {
      requestRunning = false;
    }
  }

  function render(classroom, payload) {
    const files = Array.isArray(classroom.files) ? classroom.files : [];
    if (!files.some((file) => file.id === selectedId)) {
      selectedId = classroom.professorActiveId || files[0]?.id || "";
    }
    const selected = files.find((file) => file.id === selectedId);
    const professor = files.find((file) => file.id === classroom.professorActiveId);

    setText($("className"), classroom.className);
    setText($("viewerCount"), String(classroom.viewers));
    setText($("fileCount"), String(files.length));
    setText($("mobileFileCount"), String(files.length));
    setText($("professorFile"), classroom.professorActiveName || professor?.name ||
      (classroom.broadcasting ? "선택 파일 없음" : "방송 일시정지"));
    setText($("syncStatus"), classroom.broadcasting
      ? "실시간 동기화"
      : isHost ? "일시정지 · 학생 화면에서는 코드가 숨겨집니다" : "방송 일시정지");
    setConnection(classroom.broadcasting ? "LIVE" : "일시정지", classroom.broadcasting ? "live" : "paused");

    if (selected) {
      setText($("fileName"), selected.name);
      setText($("filePath"), selected.path);
      setText($("fileType"), shortLanguage(selected.language));
      setText($("language"), selected.language);
      // 코드 본문은 실제로 바뀌었을 때만 다시 쓴다. 매번 새로 쓰면 학생이 드래그한
      // 선택 영역과 키보드 포커스가 0.75초마다 사라져서 복사조차 못 한다.
      if (rendered.fileId !== selected.id || rendered.content !== selected.content) {
        const lines = selected.content.split("\n");
        $("codeGutter").textContent = lines.map((_, index) => index + 1).join("\n");
        $("codeContent").textContent = selected.content;
        setText($("lineCount"), `${lines.length}줄`);
        rendered = { fileId: selected.id, content: selected.content };
      }
      $("emptyState").hidden = true;
      $("codeScroll").hidden = false;
    } else {
      setText($("fileName"), "공유된 파일 없음");
      setText($("filePath"), "교수님이 파일을 공유하면 여기에 표시됩니다.");
      setText($("fileType"), "···");
      setText($("language"), "Text");
      setText($("lineCount"), "0줄");
      rendered = { fileId: "", content: null };
      $("emptyState").hidden = false;
      $("codeScroll").hidden = true;
    }

    renderFiles(files, classroom.professorActiveId);
    if (isHost) renderHost(payload);
  }

  // 목록을 통째로 다시 만들지 않고 id 기준으로 맞춰 넣는다.
  // 그래야 폴링할 때마다 포커스와 선택이 날아가지 않는다.
  function renderFiles(files, professorActiveId) {
    const list = $("fileList");
    const leftover = new Map();
    for (const node of Array.from(list.children)) leftover.set(node.dataset.fileId, node);

    files.forEach((file, index) => {
      let item = leftover.get(file.id);
      if (item) leftover.delete(file.id);
      else item = createFileItem(file);
      updateFileItem(item, file, professorActiveId);
      if (list.children[index] !== item) list.insertBefore(item, list.children[index] ?? null);
    });

    for (const stale of leftover.values()) stale.remove();
  }

  function createFileItem(file) {
    const item = document.createElement("div");
    item.dataset.fileId = file.id;

    const open = document.createElement("button");
    open.type = "button";
    open.className = "file-open";
    open.addEventListener("click", () => {
      selectedId = file.id;
      localStorage.setItem("classroom-live:selected-file", selectedId);
      openFiles(false);
      void refresh();
    });

    const icon = document.createElement("span");
    icon.className = "file-icon";
    const copy = document.createElement("span");
    copy.className = "file-copy";
    const name = document.createElement("strong");
    const updated = document.createElement("small");
    copy.append(name, updated);
    const badge = document.createElement("span");
    badge.className = "professor-badge";
    badge.textContent = "교수님";
    badge.hidden = true;
    open.append(icon, copy, badge);
    item.append(open);

    let remove = null;
    if (isHost) {
      remove = document.createElement("button");
      remove.type = "button";
      remove.className = "remove-file";
      remove.textContent = "×";
      remove.addEventListener("click", async (event) => {
        event.stopPropagation();
        await fetch(`/api/host/files/${encodeURIComponent(file.id)}`, {
          method: "DELETE", headers: { "X-Admin-Token": adminToken },
        });
        await refresh();
      });
      item.append(remove);
    }

    item.parts = { open, icon, name, updated, badge, remove };
    return item;
  }

  function updateFileItem(item, file, professorActiveId) {
    const { open, icon, name, updated, badge, remove } = item.parts;
    const isSelected = file.id === selectedId;

    const className = `file-item${isSelected ? " is-selected" : ""}`;
    if (item.className !== className) item.className = className;
    if (isSelected) open.setAttribute("aria-current", "page");
    else open.removeAttribute("aria-current");

    setText(icon, shortLanguage(file.language));
    setText(name, file.name);
    setText(updated, relativeTime(file.updatedAt));
    badge.hidden = file.id !== professorActiveId;
    if (remove) remove.setAttribute("aria-label", `${file.name} 공유 목록에서 제거`);
  }

  function renderHost(payload) {
    setText($("pinValue"), payload.pin);
    setText($("toggleBroadcast"), payload.broadcasting ? "방송 일시정지" : "방송 시작");
    setText($("hostStatus"), payload.visualStudioStatus);
  }

  function setConnection(text, kind) {
    const element = $("connection");
    const className = `connection is-${kind}`;
    if (element.className !== className) element.className = className;
    setText(element.querySelector("b"), text);
  }

  function shortLanguage(language) {
    return ({ "C#": "C#", "C++": "C++", JavaScript: "JS", TypeScript: "TS", Python: "PY" })[language] || language.slice(0, 3).toUpperCase();
  }

  function relativeTime(value) {
    const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000));
    if (seconds < 10) return "방금 전";
    if (seconds < 60) return `${seconds}초 전`;
    if (seconds < 3600) return `${Math.floor(seconds / 60)}분 전`;
    return `${Math.floor(seconds / 3600)}시간 전`;
  }

  void refresh();
  setInterval(refresh, 750);
})();
