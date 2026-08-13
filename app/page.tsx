"use client";

import { useEffect, useMemo, useState } from "react";

type SharedFile = {
  id: string;
  name: string;
  path: string;
  language: string;
  updatedAt: string;
  content: string;
};

type ClassroomState = {
  className: string;
  professorActiveId: string;
  viewers: number;
  files: SharedFile[];
};

const previewState: ClassroomState = {
  className: "게임 프로그래밍 · 6주차",
  professorActiveId: "program",
  viewers: 18,
  files: [
    {
      id: "program",
      name: "Program.cs",
      path: "ClassroomLive/Program.cs",
      language: "C#",
      updatedAt: "방금 전",
      content: `using ClassroomLive.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddSingleton<ClassroomState>();
builder.Services.AddHostedService<VisualStudioWatcher>();

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/api/state", (ClassroomState state) => state.Snapshot());

app.Run("http://0.0.0.0:5050");`,
    },
    {
      id: "player",
      name: "PlayerController.cs",
      path: "Game/Scripts/PlayerController.cs",
      language: "C#",
      updatedAt: "1분 전",
      content: `using UnityEngine;

public class PlayerController : MonoBehaviour
{
    [SerializeField] private float moveSpeed = 5f;

    private Rigidbody playerBody;

    private void Awake()
    {
        playerBody = GetComponent<Rigidbody>();
    }

    private void Update()
    {
        var horizontal = Input.GetAxisRaw("Horizontal");
        var vertical = Input.GetAxisRaw("Vertical");

        var direction = new Vector3(horizontal, 0f, vertical).normalized;
        playerBody.linearVelocity = direction * moveSpeed;
    }
}`,
    },
    {
      id: "enemy",
      name: "EnemySpawner.cs",
      path: "Game/Scripts/EnemySpawner.cs",
      language: "C#",
      updatedAt: "4분 전",
      content: `using UnityEngine;

public class EnemySpawner : MonoBehaviour
{
    [SerializeField] private GameObject enemyPrefab;
    [SerializeField] private Transform[] spawnPoints;

    public void Spawn()
    {
        var point = spawnPoints[Random.Range(0, spawnPoints.Length)];
        Instantiate(enemyPrefab, point.position, point.rotation);
    }
}`,
    },
    {
      id: "settings",
      name: "GameSettings.cs",
      path: "Game/Config/GameSettings.cs",
      language: "C#",
      updatedAt: "8분 전",
      content: `namespace Game.Config;

public static class GameSettings
{
    public const int MaxPlayers = 20;
    public const float RoundDuration = 180f;
}`,
    },
  ],
};

export default function Home() {
  const [classroom, setClassroom] = useState(previewState);
  const [selectedId, setSelectedId] = useState("player");
  const [sidebarOpen, setSidebarOpen] = useState(false);
  const [connected, setConnected] = useState(false);

  useEffect(() => {
    const saved = window.localStorage.getItem("classroom-live:selected-file");
    if (saved) setSelectedId(saved);

    let active = true;
    const refresh = async () => {
      try {
        const response = await fetch("/api/state", { cache: "no-store" });
        if (!response.ok) return;
        const next = (await response.json()) as ClassroomState;
        if (active && Array.isArray(next.files)) {
          setClassroom(next);
          setConnected(true);
        }
      } catch {
        if (active) setConnected(false);
      }
    };

    void refresh();
    const timer = window.setInterval(refresh, 750);
    return () => {
      active = false;
      window.clearInterval(timer);
    };
  }, []);

  useEffect(() => {
    if (!classroom.files.some((file) => file.id === selectedId)) {
      setSelectedId(classroom.professorActiveId || classroom.files[0]?.id || "");
    }
  }, [classroom, selectedId]);

  const selected =
    classroom.files.find((file) => file.id === selectedId) ?? classroom.files[0];
  const professorFile = classroom.files.find(
    (file) => file.id === classroom.professorActiveId,
  );
  const lines = useMemo(() => selected?.content.split("\n") ?? [], [selected]);

  const chooseFile = (id: string) => {
    setSelectedId(id);
    window.localStorage.setItem("classroom-live:selected-file", id);
    setSidebarOpen(false);
  };

  return (
    <main className="shell">
      <header className="topbar">
        <div className="brand-block">
          <span className="brand-mark" aria-hidden="true">C</span>
          <div>
            <div className="brand">Classroom Live</div>
            <div className="class-name">{classroom.className}</div>
          </div>
        </div>

        <div className="session-summary">
          <span className={`connection ${connected ? "is-live" : "is-preview"}`}>
            <i aria-hidden="true" />
            {connected ? "LIVE" : "PREVIEW"}
          </span>
          <span className="viewer-count"><b>{classroom.viewers}</b>명 접속 중</span>
          <button
            className="mobile-files"
            type="button"
            aria-label="공유 파일 열기"
            aria-expanded={sidebarOpen}
            onClick={() => setSidebarOpen((open) => !open)}
          >
            파일 {classroom.files.length}
          </button>
        </div>
      </header>

      <section className="workspace">
        <article className="editor-card">
          <header className="editor-header">
            <div className="file-heading">
              <span className="file-type">CS</span>
              <div>
                <div className="file-title-row">
                  <h1>{selected?.name ?? "공유된 파일 없음"}</h1>
                  <span>내 화면</span>
                </div>
                <p>{selected?.path ?? "교수님이 파일을 공유하면 여기에 표시됩니다."}</p>
              </div>
            </div>
            <div className="professor-now" title={professorFile?.path}>
              <span className="eyebrow">교수님 보는 중</span>
              <strong><i aria-hidden="true" />{professorFile?.name ?? "없음"}</strong>
            </div>
          </header>

          <div className="code-scroll" role="region" aria-label={`${selected?.name ?? "파일"} 코드`}>
            <div className="code-gutter" aria-hidden="true">
              {lines.map((_, index) => <span key={index}>{index + 1}</span>)}
            </div>
            <pre className="code-content"><code>{selected?.content}</code></pre>
          </div>

          <footer className="statusbar">
            <span><i className="status-dot" aria-hidden="true" />실시간 동기화</span>
            <span>{selected?.language ?? "Text"}</span>
            <span>UTF-8</span>
            <span>{lines.length}줄</span>
          </footer>
        </article>

        <aside className={`file-panel ${sidebarOpen ? "is-open" : ""}`}>
          <div className="panel-heading">
            <div>
              <span className="eyebrow">이번 수업</span>
              <h2>공유 파일</h2>
            </div>
            <span className="file-count">{classroom.files.length}</span>
          </div>

          <nav className="file-list" aria-label="공유 파일 목록">
            {classroom.files.map((file) => {
              const isSelected = file.id === selected?.id;
              const isProfessorActive = file.id === classroom.professorActiveId;
              return (
                <button
                  key={file.id}
                  type="button"
                  className={`file-item ${isSelected ? "is-selected" : ""}`}
                  aria-current={isSelected ? "page" : undefined}
                  onClick={() => chooseFile(file.id)}
                >
                  <span className="file-icon">C#</span>
                  <span className="file-copy">
                    <strong>{file.name}</strong>
                    <small>{file.updatedAt}</small>
                  </span>
                  {isProfessorActive && <span className="professor-badge">교수님</span>}
                </button>
              );
            })}
          </nav>

          <div className="follow-note">
            <span aria-hidden="true">↗</span>
            <div>
              <strong>각자 원하는 파일을 볼 수 있어요</strong>
              <p>교수님이 다른 파일로 이동해도 내 화면은 그대로 유지됩니다.</p>
            </div>
          </div>
        </aside>
      </section>

      {sidebarOpen && (
        <button
          className="backdrop"
          type="button"
          aria-label="파일 목록 닫기"
          onClick={() => setSidebarOpen(false)}
        />
      )}
    </main>
  );
}
