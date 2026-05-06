import { useMutation, useQuery } from "@tanstack/react-query";
import { ArrowLeft, ArrowRight, ArrowUpFromLine, BookText, ChevronRight, ChevronsLeft, ChevronsRight, Folder, Library, Loader2, Plus, Sparkles } from "lucide-react";
import { useMemo, useState } from "react";
import { useTranslation } from "react-i18next";
import { Link, useNavigate, useParams } from "react-router-dom";
import {
  browseSource, importEntry, OpdsBook, OpdsCategory, OpdsImportMode, sourceAssetUrl,
} from "../api/opds";
import { fetchSettings } from "../api/settings";
import { useToast } from "../contexts/ToastContext";
import { clsx } from "../utils/clsx";

// Pagination rels we surface as buttons. We deliberately exclude "up" and
// "start" — those duplicate the back/up controls in the page header.
const PAGINATION_ORDER = ["first", "previous", "next", "last"] as const;
type PaginationRel = (typeof PAGINATION_ORDER)[number];
const PAGINATION_RELS = new Set<string>(PAGINATION_ORDER);
const PAGINATION_ICONS: Record<PaginationRel, typeof ChevronsLeft> = {
  first: ChevronsLeft,
  previous: ArrowLeft,
  next: ArrowRight,
  last: ChevronsRight,
};

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

  const settings = useQuery({ queryKey: ["settings"], queryFn: fetchSettings });
  const aiEnabled = settings.data?.app.aiEnabled ?? true;

  const importBook = useMutation({
    mutationFn: ({ href, title, mode }: { href: string; title: string; mode: OpdsImportMode }) =>
      importEntry(id, href, title, mode),
    onSuccess: ({ novelId }, vars) => {
      toast.success(vars.mode === "AddAndRunAi"
        ? t("browse.addedAndQueued")
        : t("browse.added"));
      nav(`/novels/${novelId}`);
    },
    onError: (e) => toast.error(t("browse.addFailed"), e instanceof Error ? e.message : ""),
  });

  function openCategory(c: OpdsCategory) {
    setStack((s) => [...s, { url: c.href, title: c.title }]);
  }
  function pop() { setStack((s) => (s.length > 1 ? s.slice(0, -1) : s)); }

  // Many OPDS catalogs omit the title attribute on next/previous links — we
  // can't drop those silently or pagination disappears. Fall back to an i18n
  // label keyed off the rel and keep the buttons in a stable order regardless
  // of how the server happened to emit them.
  const paginationLinks = useMemo(() => {
    const seen = new Map<PaginationRel, { rel: PaginationRel; href: string; title: string }>();
    for (const l of feed.data?.navigationLinks ?? []) {
      if (!l.rel || !PAGINATION_RELS.has(l.rel)) continue;
      const rel = l.rel as PaginationRel;
      if (seen.has(rel)) continue;
      seen.set(rel, {
        rel,
        href: l.href,
        title: l.title?.trim() || t(`browse.page.${rel}`),
      });
    }
    return PAGINATION_ORDER.flatMap((r) => {
      const v = seen.get(r);
      return v ? [v] : [];
    });
  }, [feed.data?.navigationLinks, t]);

  const isEmpty =
    feed.data
    && (feed.data.categories.length === 0)
    && (feed.data.books.length === 0);

  // Track which book + which mode is in flight so we can disable just
  // that pair of buttons rather than all of them.
  const busy = importBook.isPending ? importBook.variables : null;

  return (
    <div className="space-y-6">
      {/* Two-row header on mobile so a long feed title doesn't fight with
          the back/up buttons for space. */}
      <div className="flex flex-col gap-2 sm:flex-row sm:flex-wrap sm:items-center">
        <div className="flex shrink-0 items-center gap-2">
          {/* Distinct icon (`Library`) for "back to sources list" so it
              doesn't visually match the in-feed Up button. */}
          <Link className="btn-ghost shrink-0 px-2 py-1.5 sm:px-3 sm:py-2" to="/sources">
            <Library className="h-4 w-4" />
            <span className="hidden sm:inline">{t("browse.back")}</span>
          </Link>
          {stack.length > 1 && (
            <button className="btn-ghost shrink-0 px-2 py-1.5 sm:px-3 sm:py-2" onClick={pop}>
              <ArrowUpFromLine className="h-4 w-4" />
              <span className="hidden sm:inline">{t("browse.up")}</span>
            </button>
          )}
        </div>
        <h1 className="min-w-0 break-words text-base font-semibold leading-tight tracking-tight sm:flex-1 sm:truncate sm:text-2xl">
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

          {feed.data.books.length > 0 && (
            <div className="grid grid-cols-1 gap-3 sm:grid-cols-2 lg:grid-cols-3">
              {feed.data.books.map((b, i) => (
                <BookCard
                  key={i}
                  sourceId={id}
                  book={b}
                  aiEnabled={aiEnabled}
                  busyMode={
                    busy && busy.href === (b.acquisitionLinks.find((l) => l.type?.includes("epub")) ?? b.acquisitionLinks[0])?.href
                      ? busy.mode : null
                  }
                  onImport={(href, mode) => importBook.mutate({ href, title: b.title, mode })}
                />
              ))}
            </div>
          )}

          {paginationLinks.length > 0 && (
            <div className="flex flex-wrap items-center justify-end gap-2 pt-2">
              {paginationLinks.map((l) => {
                const Icon = PAGINATION_ICONS[l.rel];
                const isPrev = l.rel === "first" || l.rel === "previous";
                return (
                  <button
                    key={l.rel}
                    className="btn-secondary"
                    // Paginate in place — clicking Next shouldn't grow the
                    // back-stack with every page so Up still pops the user
                    // out of the feed they're paging through.
                    onClick={() =>
                      setStack((s) => [
                        ...s.slice(0, -1),
                        { url: l.href, title: current?.title },
                      ])
                    }
                  >
                    {isPrev && <Icon className="h-4 w-4" />}
                    {l.title}
                    {!isPrev && <Icon className="h-4 w-4" />}
                  </button>
                );
              })}
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

interface BookCardProps {
  sourceId: string;
  book: OpdsBook;
  aiEnabled: boolean;
  busyMode: OpdsImportMode | null;
  onImport: (href: string, mode: OpdsImportMode) => void;
}

function BookCard({ sourceId, book, aiEnabled, busyMode, onImport }: BookCardProps) {
  const { t } = useTranslation();
  const epub = book.acquisitionLinks.find((l) => l.type?.includes("epub"));
  const link = epub ?? book.acquisitionLinks[0];
  const isEpub = !!epub;

  const [tagsExpanded, setTagsExpanded] = useState(false);
  const [summaryExpanded, setSummaryExpanded] = useState(false);

  // Auto-detect HTML even when the catalog forgets the type=html attribute.
  const renderedSummary = useMemo(
    () => formatSummary(book.summary, book.summaryIsHtml),
    [book.summary, book.summaryIsHtml],
  );

  const TAG_PREVIEW = 6;
  const visibleTags = tagsExpanded ? book.categories : book.categories.slice(0, TAG_PREVIEW);

  return (
    <div className="card flex flex-col gap-3 p-4">
      <div className="flex items-start gap-3">
        <div className="grid h-20 w-14 flex-shrink-0 place-items-center overflow-hidden rounded bg-stone-100 dark:bg-stone-700">
          {book.coverHref
            ? <img src={sourceAssetUrl(sourceId, book.coverHref)} alt="" className="h-full w-full object-cover" />
            : <BookText className="h-4 w-4 text-stone-400" />}
        </div>
        <div className="min-w-0 flex-1">
          <div className="line-clamp-2 text-sm font-semibold">{book.title}</div>
          <div className="truncate text-xs text-stone-500 dark:text-stone-400">
            {book.author ?? t("browse.unknownAuthor")}
          </div>
          {(book.languages.length > 0 || book.publisher || book.issued) && (
            <div className="mt-1 flex flex-wrap items-center gap-x-2 gap-y-0.5 text-[10px] uppercase tracking-wide text-stone-400 dark:text-stone-500">
              {book.languages.map((l) => <span key={l}>{l}</span>)}
              {book.publisher && <span className="normal-case">{book.publisher}</span>}
              {book.issued && <span className="normal-case">{shortDate(book.issued)}</span>}
            </div>
          )}
        </div>
      </div>

      {book.categories.length > 0 && (
        <div className="flex flex-wrap items-center gap-1">
          {visibleTags.map((c) => (
            <span key={c} className="rounded bg-stone-100 px-1.5 py-0.5 text-[10px] font-medium text-stone-600 dark:bg-stone-700 dark:text-stone-300">
              {c}
            </span>
          ))}
          {book.categories.length > TAG_PREVIEW && (
            <button
              type="button"
              onClick={() => setTagsExpanded((x) => !x)}
              className="rounded bg-stone-100/70 px-1.5 py-0.5 text-[10px] font-medium text-stone-500 hover:bg-stone-200 hover:text-stone-700 dark:bg-stone-700/60 dark:text-stone-400 dark:hover:bg-stone-700 dark:hover:text-stone-200"
            >
              {tagsExpanded
                ? t("browse.tagsCollapse")
                : `+${book.categories.length - TAG_PREVIEW}`}
            </button>
          )}
        </div>
      )}

      {renderedSummary && (
        <div>
          <div
            // No more border above the buttons — the description's clamp
            // already separates it from them, the line was visual noise.
            className={clsx(
              "text-xs leading-relaxed text-stone-600 dark:text-stone-400 [&_a]:underline [&_em]:italic [&_p]:my-0.5 [&_strong]:font-semibold",
              !summaryExpanded && "line-clamp-4",
            )}
            dangerouslySetInnerHTML={{ __html: renderedSummary }}
          />
          {/* Heuristic: only show "See more" when the content is plausibly
              longer than the line-clamp window. Cheap proxy by length. */}
          {renderedSummary.length > 240 && (
            <button
              type="button"
              onClick={() => setSummaryExpanded((x) => !x)}
              className="mt-1 text-[11px] font-medium text-stone-500 hover:text-stone-900 hover:underline dark:text-stone-400 dark:hover:text-stone-100"
            >
              {summaryExpanded ? t("browse.seeLess") : t("browse.seeMore")}
            </button>
          )}
        </div>
      )}

      <div className="mt-auto flex flex-col gap-1.5">
        <button
          className={clsx(
            aiEnabled ? "btn-secondary" : "btn-primary",
            "w-full justify-center text-sm",
            !link && "cursor-not-allowed opacity-50",
          )}
          disabled={!link || busyMode !== null}
          onClick={() => link && onImport(link.href, "AddOnly")}
        >
          {busyMode === "AddOnly"
            ? <Loader2 className="h-4 w-4 animate-spin" />
            : <Plus className="h-4 w-4" />}
          {!link ? t("browse.noLink") : isEpub ? t("browse.add") : t("browse.addNonEpub")}
        </button>
        {aiEnabled && (
          <button
            className={clsx("btn-primary w-full justify-center text-sm", !link && "cursor-not-allowed opacity-50")}
            disabled={!link || busyMode !== null}
            onClick={() => link && onImport(link.href, "AddAndRunAi")}
          >
            {busyMode === "AddAndRunAi"
              ? <Loader2 className="h-4 w-4 animate-spin" />
              : <Sparkles className="h-4 w-4" />}
            {t("browse.addAndRunAi")}
          </button>
        )}
      </div>
    </div>
  );
}

/**
 * Coerce the OPDS summary into something we can stuff into innerHTML. If
 * the server flagged the entry as HTML or the body looks like markup, we
 * trust it; otherwise we escape and convert paragraph breaks into <br>
 * so the line-clamp respects them.
 */
function formatSummary(raw: string | null, declaredHtml: boolean): string | null {
  if (!raw) return null;
  const looksLikeHtml = /<\/?[a-z][\s\S]*>/i.test(raw);
  if (declaredHtml || looksLikeHtml) return raw;
  return escapeHtml(raw).replace(/\n/g, "<br>");
}

function escapeHtml(s: string): string {
  return s
    .replace(/&/g, "&amp;")
    .replace(/</g, "&lt;")
    .replace(/>/g, "&gt;")
    .replace(/"/g, "&quot;")
    .replace(/'/g, "&#39;");
}

function shortDate(raw: string): string {
  const d = new Date(raw);
  if (Number.isNaN(d.getTime())) return raw;
  return d.getFullYear().toString();
}
