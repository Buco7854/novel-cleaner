import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from "@microsoft/signalr";

export interface BookLogEvent {
  bookId: string;
  timestamp: string;
  level: string;
  message: string;
  detail?: string | null;
  groupId?: string | null;
}
export interface BookStatusEvent {
  bookId: string;
  status: string;
  progress: number | null;
  done: number | null;
  total: number | null;
}

export interface BookHubHandle {
  conn: HubConnection;
  start(): Promise<void>;
  subscribe(bookId: string): Promise<void>;
  unsubscribe(bookId: string): Promise<void>;
  stop(): Promise<void>;
  onLog(cb: (e: BookLogEvent) => void): () => void;
  onStatus(cb: (e: BookStatusEvent) => void): () => void;
}

export function createBookHub(): BookHubHandle {
  const conn = new HubConnectionBuilder()
    .withUrl("/hubs/books")
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  return {
    conn,
    async start() {
      if (conn.state === HubConnectionState.Disconnected) await conn.start();
    },
    subscribe(bookId)   { return conn.invoke("SubscribeBook", bookId); },
    unsubscribe(bookId) { return conn.invoke("UnsubscribeBook", bookId); },
    stop()               { return conn.stop(); },
    onLog(cb)            { conn.on("log", cb); return () => conn.off("log", cb); },
    onStatus(cb)         { conn.on("status", cb); return () => conn.off("status", cb); },
  };
}
