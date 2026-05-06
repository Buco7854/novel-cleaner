import { useMutation, useQuery } from "@tanstack/react-query";
import { ArrowLeft, BookText, ChevronRight, Folder, Loader2, Sparkles } from "lucide-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router-dom";
import { browseSource, importEntry, OpdsBook, OpdsCategory, sourceAssetUrl } from "../api/opds";
import { useToast } from "../contexts/ToastContext";
import { clsx } from "../utils/clsx";

const PAGINATION_RELS = new Set(["next", "previous", "first", "last", "up", "start"]);

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
    onSuccess: ({ jobId }) => { toast.success(t("browse.cleanQueued")); nav(`/jobs/${jobId}`); },
    onError: (e) => toast.error(t("browse.cleanFailed"), e instanceof Error ? e.message : ""),
  });

  function openCategory(c: OpdsCategory) {
    setStack((s) => [...s, { url: c.href, title: c.title }]);
  }
  function pop() { setStack((s) => (s.length > 1 ? s.slice(0, -1) : s)); }

  // Pagination links from the feed level (next/previous/up/start) — render
  // separately from category cards.
  const paginationLinks = (feed.data?.navigationLinks ?? []).filter(
    (l) => l.rel && PAGINATION_RELS.has(l.rel) && l.title && l.rel !== "self"
  );

  const isEmpty =
    feed.data
    && (feed.data.categories.length === 0)
    && (feed.data.books.length === 0);

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
          {/* Categories (folders) */}
          {feed.data.categories.length > 0 && (
            <div className="space-y-2">
              {feed.data.categories.map((c, i) => (
                <button
                  key={i}
                  onClick={() => openCategory(c)}
                  className="card flex w-full items-center gap-3 px-4 py-3 text-left transition-colors hover:ring-stone-300 dark:hover:ring-stone-600"
                >
                  <Folder className="h-4 w-4 shrink-0 text-stone-400" />
                  <div className="min-w-0 flex-1">
                    <div className="truncate text-sm font-medium">{c.title}</div>
                    {c.summary && (
                      <div className="truncate text-xs text-stone-500 dark:text-stone-400">{c.summary}</div>
                    )}
                  </div>
                  <ChevronRight className="h-4 w-4 shrink-0 text-stone-400" />
                </button>
              ))}
            </div>
          )}

          {/* Books */}
          {feed.data.books.length > 0 && (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {feed.data.books.map((b, i) => (
                <BookCard key={i}
                  sourceId={id}
                  book={b}
                  onClean={(href, title) => importBook.mutate({ href, title })}
                  cleaning={importBook.isPending} />
              ))}
            </div>
          )}

          {/* Pagination footer */}
          {paginationLinks.length > 0 && (
            <div className="flex flex-wrap items-center justify-end gap-2 pt-2">
              {paginationLinks.map((l, i) => (
                <button
                  key={i}
                  className="btn-secondary"
                  onClick={() => setStack((s) => [...s, { url: l.href, title: l.title ?? undefined }])}
                >
                  {l.title}
                </button>
              ))}
            </div>
          )}

          {isEmpty && (
            <div className="card grid place-items-center p-10 text-sm text-stone-500">
              {t("browse.empty")}
            </div>
          )}
        </>
      )}
    </div>
  );
}

function BookCard({ sourceId, book, onClean, cleaning }: {
  sourceId: string;
  book: OpdsBook;
  onClean: (href: string, title: string) => void;
  cleaning: boolean;
}) {
  const { t } = useTranslation();
  const epub = book.acquisitionLinks.find((l) => l.type?.includes("epub"));
  const fallback = book.acquisitionLinks[0];
  const link = epub ?? fallback;
  const isEpub = !!epub;

  return (
    <div className="card flex flex-col p-4">
      <div className="flex items-start gap-3">
        <div className="grid h-16 w-12 flex-shrink-0 place-items-center overflow-hidden rounded bg-stone-100 dark:bg-stone-700">
          {book.coverHref
            ? <img src={sourceAssetUrl(sourceId, book.coverHref)} alt="" className="h-full w-full object-cover" />
            : <BookText className="h-4 w-4 text-stone-400" />}
        </div>
        <div className="min-w-0 flex-1">
          <div className="line-clamp-2 text-sm font-semibold">{book.title}</div>
          <div className="truncate text-xs text-stone-500 dark:text-stone-400">
            {book.author ?? t("browse.unknownAuthor")}
          </div>
        </div>
      </div>
      {book.summary && (
        <p className="mt-2 line-clamp-3 text-xs text-stone-600 dark:text-stone-400">{book.summary}</p>
      )}
      <button
        className={clsx("btn-primary mt-auto pt-2", !link && "opacity-50 cursor-not-allowed")}
        style={{ marginTop: "auto" }}
        disabled={!link || cleaning}
        onClick={() => link && onClean(link.href, book.title)}
      >
        {cleaning
          ? <Loader2 className="h-4 w-4 animate-spin" />
          : <Sparkles className="h-4 w-4" />}
        {!link
          ? t("browse.noLink")
          : isEpub
            ? t("browse.cleanBook")
            : t("browse.cleanNonEpub")}
      </button>
    </div>
  );
}
