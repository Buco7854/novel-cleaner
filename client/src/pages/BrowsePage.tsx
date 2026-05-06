import { useMutation, useQuery } from "@tanstack/react-query";
import { ArrowLeft, BookText, ChevronRight, Download, Folder, Loader2 } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router-dom";
import { browseSource, importEntry, OpdsEntry, OpdsLink } from "../api/opds";
import { useToast } from "../contexts/ToastContext";
import { clsx } from "../utils/clsx";

const NAV_RELS = ["subsection", "next", "previous", "self", "start", "up"];

export function BrowsePage() {
  const { t } = useTranslation();
  const { id = "" } = useParams();
  const toast = useToast();
  const nav = useNavigate();
  const [stack, setStack] = useState<{ url?: string; title?: string }[]>([{}]);
  const current = stack[stack.length - 1];

  const feed = useQuery({
    queryKey: ["browse", id, current?.url ?? ""],
    queryFn: () => browseSource(id, current?.url),
    enabled: !!id,
  });

  const importBook = useMutation({
    mutationFn: ({ href, title }: { href: string; title?: string }) => importEntry(id, href, title),
    onSuccess: ({ jobId }) => { toast.success(t("browse.title")); nav(`/jobs/${jobId}`); },
    onError: (e) => toast.error("Import failed", e instanceof Error ? e.message : ""),
  });

  function open(link: OpdsLink) { setStack((s) => [...s, { url: link.href, title: link.title ?? undefined }]); }
  function pop() { setStack((s) => (s.length > 1 ? s.slice(0, -1) : s)); }

  return (
    <div className="space-y-6">
      <div className="flex flex-wrap items-center gap-2">
        <Link className="btn-ghost shrink-0" to="/sources">
          <ArrowLeft className="h-4 w-4" /> {t("browse.back")}
        </Link>
        {stack.length > 1 && (
          <button className="btn-ghost shrink-0" onClick={pop}>
            <ArrowLeft className="h-4 w-4" /> {t("browse.up")}
          </button>
        )}
        <h1 className="ml-2 min-w-0 flex-1 truncate text-xl font-semibold tracking-tight sm:text-2xl">
          {feed.data?.title ?? current?.title ?? t("browse.title")}
        </h1>
      </div>

      {feed.isLoading && (
        <div className="flex items-center gap-2 text-sm text-stone-500">
          <Loader2 className="h-4 w-4 animate-spin" /> {t("browse.loading")}
        </div>
      )}

      {feed.error && (
        <div className="card border-rose-200 bg-rose-50 p-4 text-sm text-rose-700 dark:bg-rose-500/10 dark:text-rose-300">
          {feed.error instanceof Error ? feed.error.message : "Could not load feed."}
        </div>
      )}

      {feed.data && (
        <>
          {feed.data.navigationLinks
            .filter((l) => l.rel && NAV_RELS.includes(l.rel) && l.title)
            .map((l, i) => (
              <button key={i} onClick={() => open(l)}
                className="flex w-full items-center gap-3 rounded-md card px-4 py-3 text-left hover:ring-stone-300 dark:hover:ring-stone-600">
                <Folder className="h-4 w-4 text-stone-400" />
                <span className="flex-1 text-sm">{l.title}</span>
                <ChevronRight className="h-4 w-4 text-stone-400" />
              </button>
          ))}

          {feed.data.entries.length > 0 && (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {feed.data.entries.map((e, i) => (
                <Entry key={i} entry={e}
                  onOpen={(href, title) => importBook.mutate({ href, title })}
                  importing={importBook.isPending} />
              ))}
            </div>
          )}
          {feed.data.entries.length === 0 && feed.data.navigationLinks.length === 0 && (
            <div className="card grid place-items-center p-10 text-sm text-stone-500">
              {t("browse.empty")}
            </div>
          )}
        </>
      )}
    </div>
  );
}

function Entry({ entry, onOpen, importing }: { entry: OpdsEntry; onOpen: (href: string, title: string) => void; importing: boolean }) {
  const { t } = useTranslation();
  const epub = entry.acquisitionLinks.find((l) => l.type?.includes("epub"));
  const fallback = entry.acquisitionLinks[0];
  const link = epub ?? fallback;

  return (
    <div className="card p-4">
      <div className="flex items-start gap-3">
        <div className="grid h-12 w-9 flex-shrink-0 place-items-center overflow-hidden rounded bg-stone-100 dark:bg-stone-700">
          {entry.coverHref
            ? <img src={entry.coverHref} alt="" className="h-full w-full object-cover" />
            : <BookText className="h-4 w-4 text-stone-400" />}
        </div>
        <div className="min-w-0">
          <div className="line-clamp-2 text-sm font-semibold">{entry.title}</div>
          <div className="truncate text-xs text-stone-500 dark:text-stone-400">{entry.author ?? t("browse.unknownAuthor")}</div>
        </div>
      </div>
      {entry.summary && (
        <p className="mt-2 line-clamp-3 text-xs text-stone-600 dark:text-stone-400">{entry.summary}</p>
      )}
      <button
        className={clsx("btn-primary mt-3 w-full", !link && "opacity-50 cursor-not-allowed")}
        disabled={!link || importing}
        onClick={() => link && onOpen(link.href, entry.title)}
      >
        <Download className="h-4 w-4" />
        {epub ? t("browse.importEpub") : link ? t("browse.import") : t("browse.noLink")}
      </button>
    </div>
  );
}
