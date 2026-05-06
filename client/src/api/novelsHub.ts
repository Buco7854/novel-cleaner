import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

export interface NovelLogEvent {
  novelId: string;
  timestamp: string;
  level: string;
  message: string;
  detail?: string | null;
  groupId?: string | null;
}
export interface NovelStatusEvent {
  novelId: string;
  status: string;
  progress: number | null;
  done: number | null;
  total: number | null;
}

export interface NovelHubHandle {
  conn: HubConnection;
  start(): Promise<void>;
  subscribe(novelId: string): Promise<void>;
  unsubscribe(novelId: string): Promise<void>;
  stop(): Promise<void>;
  onLog(cb: (e: NovelLogEvent) => void): () => void;
  onStatus(cb: (e: NovelStatusEvent) => void): () => void;
}

export function createNovelHub(): NovelHubHandle {
  const conn = new HubConnectionBuilder()
    .withUrl("/hubs/novels")
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  return {
    conn,
    async start() {
      if (conn.state === HubConnectionState.Disconnected) await conn.start();
    },
    subscribe(novelId)   { return conn.invoke("SubscribeNovel", novelId); },
    unsubscribe(novelId) { return conn.invoke("UnsubscribeNovel", novelId); },
    stop()               { return conn.stop(); },
    onLog(cb)            { conn.on("log", cb); return () => conn.off("log", cb); },
    onStatus(cb)         { conn.on("status", cb); return () => conn.off("status", cb); },
  };
}
