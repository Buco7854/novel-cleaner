/** @type {import('tailwindcss').Config} */
export default {
  content: ["./index.html", "./src/**/*.{ts,tsx}"],
  darkMode: "class",
  theme: {
    extend: {
      fontFamily: {
        sans: ['Inter', 'ui-sans-serif', 'system-ui', '-apple-system', 'BlinkMacSystemFont', 'Segoe UI', 'sans-serif'],
        mono: ['ui-monospace', 'SFMono-Regular', '"Cascadia Mono"', 'Menlo', 'monospace'],
      },
      colors: {
        // App-wide light-mode body: a paper-warm cream that reads as
        // "book page" against the cards. Pure white sits on it with a
        // visibly harsh "cold edge", so we use `paper` (a creamy white)
        // for raised surfaces — same warmth as the body, just one shade
        // lighter so cards/inputs/dropdowns still read as elevated.
        cream: "#fbf8f1", // body
        paper: "#fefcf7", // cards, inputs, dropdowns, header
      },
    },
  },
  plugins: [],
};
