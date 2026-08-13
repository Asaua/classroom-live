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
  const viewerId = localStorage.getItem("classroom-live:viewer") || crypto.randomUUID();
  localStorage.setItem("classroom-live:viewer", viewerId);

  if (isHost) $("hostControls").hidden = false;
  if (!isHost && !pin) $("gate").hidden = false;

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

  async function refresh() {
    if (requestRunning) return;
    requestRunning = true;
    try {
      const response = await fetch(isHost ? "/api/host/state" : "/api/state", {
        cache: "no-store",
        headers: isHost
          ? { "X-Admin-Token": adminToken }
          : { "X-Classroom-Pin": pin, "X-Viewer-Id": viewerId },
      });

      if (!response.ok) {
        if (!isHost && response.status === 401) {
          $("gate").hidden = false;
          if (pin) $("gateError").textContent = "PIN이 맞지 않습니다.";
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
      $("syncStatus").textContent = "서버 연결 대기 중";
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

    $("className").textContent = classroom.className;
    $("viewerCount").textContent = classroom.viewers;
    $("fileCount").textContent = files.length;
    $("mobileFileCount").textContent = files.length;
    $("professorFile").textContent = classroom.professorActiveName || professor?.name || (classroom.broadcasting ? "선택 파일 없음" : "방송 일시정지");
    $("syncStatus").textContent = classroom.broadcasting ? "실시간 동기화" : "방송 일시정지";
    setConnection(classroom.broadcasting ? "LIVE" : "일시정지", classroom.broadcasting ? "live" : "paused");

    if (selected) {
      const lines = selected.content.split("\n");
      $("fileName").textContent = selected.name;
      $("filePath").textContent = selected.path;
      $("fileType").textContent = shortLanguage(selected.language);
      $("language").textContent = selected.language;
      $("lineCount").textContent = `${lines.length}줄`;
      $("codeGutter").textContent = lines.map((_, index) => index + 1).join("\n");
      $("codeContent").textContent = selected.content;
      $("emptyState").hidden = true;
      $("codeScroll").hidden = false;
    } else {
      $("fileName").textContent = "공유된 파일 없음";
      $("filePath").textContent = "교수님이 파일을 공유하면 여기에 표시됩니다.";
      $("fileType").textContent = "···";
      $("language").textContent = "Text";
      $("lineCount").textContent = "0줄";
      $("emptyState").hidden = false;
      $("codeScroll").hidden = true;
    }

    renderFiles(files, classroom.professorActiveId);
    if (isHost) renderHost(payload);
  }

  function renderFiles(files, professorActiveId) {
    const list = $("fileList");
    list.replaceChildren();
    for (const file of files) {
      const item = document.createElement("div");
      item.className = `file-item${file.id === selectedId ? " is-selected" : ""}`;
      const open = document.createElement("button");
      open.type = "button";
      open.className = "file-open";
      if (file.id === selectedId) open.setAttribute("aria-current", "page");

      const icon = document.createElement("span");
      icon.className = "file-icon";
      icon.textContent = shortLanguage(file.language);
      const copy = document.createElement("span");
      copy.className = "file-copy";
      const name = document.createElement("strong");
      name.textContent = file.name;
      const updated = document.createElement("small");
      updated.textContent = relativeTime(file.updatedAt);
      copy.append(name, updated);
      open.append(icon, copy);

      if (file.id === professorActiveId) {
        const badge = document.createElement("span");
        badge.className = "professor-badge";
        badge.textContent = "교수님";
        open.append(badge);
      }
      item.append(open);
      if (isHost) {
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "remove-file";
        remove.setAttribute("aria-label", `${file.name} 공유 목록에서 제거`);
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

      open.addEventListener("click", () => {
        selectedId = file.id;
        localStorage.setItem("classroom-live:selected-file", selectedId);
        openFiles(false);
        void refresh();
      });
      list.append(item);
    }
  }

  function renderHost(payload) {
    $("pinValue").textContent = payload.pin;
    $("toggleBroadcast").textContent = payload.broadcasting ? "방송 일시정지" : "방송 시작";
    $("hostStatus").textContent = payload.visualStudioStatus;
  }

  function setConnection(text, kind) {
    const element = $("connection");
    element.className = `connection is-${kind}`;
    element.querySelector("b").textContent = text;
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
