import { Listbox, ListboxButton, ListboxOption, ListboxOptions, Transition } from "@headlessui/react";
import { Check, ChevronDown } from "lucide-react";
import { Fragment } from "react";
import { clsx } from "../utils/clsx";

interface Option<T extends string> {
  value: T;
  label: string;
}

interface Props<T extends string> {
  value: T;
  onChange: (v: T) => void;
  options: Option<T>[];
  disabled?: boolean;
}

export function Select<T extends string>({ value, onChange, options, disabled }: Props<T>) {
  const selected = options.find((o) => o.value === value);
  return (
    <Listbox value={value} onChange={onChange} disabled={disabled}>
      <div className="relative">
        <ListboxButton
          className={clsx(
            "input flex items-center justify-between text-left",
            disabled && "cursor-not-allowed opacity-50"
          )}
        >
          <span>{selected?.label ?? value}</span>
          <ChevronDown className="h-4 w-4 shrink-0 text-stone-400" />
        </ListboxButton>
        <Transition
          as={Fragment}
          enter="transition ease-out duration-100"
          enterFrom="opacity-0 translate-y-[-4px]"
          enterTo="opacity-100 translate-y-0"
          leave="transition ease-in duration-75"
          leaveFrom="opacity-100 translate-y-0"
          leaveTo="opacity-0 translate-y-[-4px]"
        >
          <ListboxOptions className="absolute z-10 mt-1 w-full rounded-lg border border-stone-200 bg-white p-1 shadow-md dark:border-stone-700 dark:bg-stone-800">
            {options.map((opt) => (
              <ListboxOption
                key={opt.value}
                value={opt.value}
                className={({ focus }) =>
                  clsx(
                    "flex cursor-pointer items-center justify-between rounded-md px-3 py-2 text-sm",
                    focus && "bg-stone-100 dark:bg-stone-700",
                    opt.value === value
                      ? "font-medium text-stone-900 dark:text-stone-100"
                      : "text-stone-600 dark:text-stone-300"
                  )
                }
              >
                <span>{opt.label}</span>
                {opt.value === value && <Check className="h-3.5 w-3.5 text-stone-500" />}
              </ListboxOption>
            ))}
          </ListboxOptions>
        </Transition>
      </div>
    </Listbox>
  );
}
