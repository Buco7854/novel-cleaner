import i18n from "i18next";
import { initReactI18next } from "react-i18next";
import en from "./locales/en.json";
import fr from "./locales/fr.json";

const saved = localStorage.getItem("lang");
const detected = saved ?? (navigator.language.toLowerCase().startsWith("fr") ? "fr" : "en");

i18n.use(initReactI18next).init({
  resources: { en: { translation: en }, fr: { translation: fr } },
  lng: detected,
  fallbackLng: "en",
  interpolation: { escapeValue: false },
});

i18n.on("languageChanged", (lng) => {
  localStorage.setItem("lang", lng);
  document.documentElement.setAttribute("lang", lng);
});

document.documentElement.setAttribute("lang", detected);

export default i18n;
