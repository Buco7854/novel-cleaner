import { Listbox, ListboxButton, ListboxOption, ListboxOptions, Transition } from "@headlessui/react";
import { Check, ChevronDown } from "lucide-react";
import { Fragment } from "react";
import { useTranslation } from "react-i18next";
import i18n from "../i18n";
import { clsx } from "../utils/clsx";

const LANGS = [
  { value: "en", label: "English" },
  { value: "fr", label: "Français" },
];

export function LangToggle() {
  const { i18n: { resolvedLanguage } } = useTranslation();
  const current = resolvedLanguage ?? "en";

  return (
    <Listbox value={current} onChange={(lng) => i18n.changeLanguage(lng)}>
      <div className="relative">
        <ListboxButton className="flex items-center gap-1.5 rounded-md border border-stone-200 px-2.5 py-1.5 text-xs font-medium uppercase text-stone-600 hover:bg-stone-50 dark:border-stone-700 dark:text-stone-300 dark:hover:bg-stone-800">
          {current}
          <ChevronDown className="h-3 w-3 text-stone-400" />
        </ListboxButton>
        <Transition
          as={Fragment}
          enter="transition ease-out duration-100"
          enterFrom="opacity-0 scale-95"
          enterTo="opacity-100 scale-100"
          leave="transition ease-in duration-75"
          leaveFrom="opacity-100 scale-100"
          leaveTo="opacity-0 scale-95"
        >
          <ListboxOptions className="absolute right-0 z-50 mt-1 w-36 origin-top-right rounded-lg border border-stone-200 bg-paper p-1 shadow-sm dark:border-stone-700 dark:bg-stone-800">
            {LANGS.map((l) => (
              <ListboxOption
                key={l.value}
                value={l.value}
                className={({ focus }) =>
                  clsx(
                    "flex cursor-pointer items-center justify-between rounded-md px-2.5 py-1.5 text-sm",
                    focus && "bg-stone-100 dark:bg-stone-700",
                    current === l.value
                      ? "font-medium text-stone-900 dark:text-stone-100"
                      : "text-stone-600 dark:text-stone-300"
                  )
                }
              >
                {l.label}
                {current === l.value && <Check className="h-3.5 w-3.5 text-stone-500" />}
              </ListboxOption>
            ))}
          </ListboxOptions>
        </Transition>
      </div>
    </Listbox>
  );
}
