import { CloudUpload } from "lucide-react";
import { DragEvent, useRef, useState } from "react";
import { clsx } from "../utils/clsx";

interface Props {
  accept?: string;
  multiple?: boolean;
  onFiles: (files: File[]) => void;
  hint?: string;
  disabled?: boolean;
}

export function Dropzone({ accept = ".epub", multiple = true, onFiles, hint, disabled }: Props) {
  const inputRef = useRef<HTMLInputElement>(null);
  const [drag, setDrag] = useState(false);

  function handle(files: FileList | null) {
    if (!files || files.length === 0) return;
    onFiles(Array.from(files));
  }
  function onDrop(e: DragEvent<HTMLLabelElement>) {
    e.preventDefault(); setDrag(false);
    if (disabled) return;
    handle(e.dataTransfer.files);
  }

  return (
    <label
      onDragOver={(e) => { e.preventDefault(); if (!disabled) setDrag(true); }}
      onDragLeave={() => setDrag(false)}
      onDrop={onDrop}
      className={clsx(
        "flex cursor-pointer flex-col items-center justify-center gap-3 rounded-xl border-2 border-dashed px-6 py-10 text-center transition-colors sm:px-10 sm:py-12",
        drag
          ? "border-stone-700 bg-stone-100 dark:border-stone-300 dark:bg-stone-800"
          : "border-stone-300 hover:border-stone-500 hover:bg-stone-50 dark:border-stone-700 dark:hover:border-stone-500 dark:hover:bg-stone-800/40",
        disabled && "cursor-not-allowed opacity-50"
      )}
    >
      <div className="grid h-12 w-12 place-items-center rounded-full bg-stone-100 ring-1 ring-stone-200 dark:bg-stone-800 dark:ring-stone-700">
        <CloudUpload className="h-5 w-5 text-stone-500 dark:text-stone-400" strokeWidth={1.75} />
      </div>
      <div className="space-y-1">
        <div className="text-sm font-medium text-stone-700 dark:text-stone-200">
          Drop EPUB files here, or <span className="underline underline-offset-2 decoration-stone-400">click to browse</span>
        </div>
        {hint && <div className="text-xs text-stone-500 dark:text-stone-500">{hint}</div>}
      </div>
      <input ref={inputRef} type="file" className="sr-only" accept={accept} multiple={multiple}
        disabled={disabled} onChange={(e) => handle(e.target.files)} />
    </label>
  );
}
