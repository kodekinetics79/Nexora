import i18n from 'i18next';
import { initReactI18next } from 'react-i18next';
import LanguageDetector from 'i18next-browser-languagedetector';

const resources = {
  en: {
    translation: {
      "dashboard": "Dashboard",
      "procurement": "Procurement",
      "quotations": "Quotations",
      "orders": "Orders",
      "shipments": "Shipments",
      "setup_master": "Setup Master",
      "welcome_back": "Welcome back",
      "login_subtitle": "Enter your operational credentials to continue.",
      "search_placeholder": "Search anything...",
      "account_profile": "Account Profile",
      "color_theme": "Color Theme",
      "system_settings": "System Settings",
      "logout": "Log Out Session",
      "create_new": "Create New",
      "sync": "Sync",
      "live": "Live",
      "language": "Language"
    }
  },
  ar: {
    translation: {
      "dashboard": "لوحة القيادة",
      "procurement": "المشتريات",
      "quotations": "العروض",
      "orders": "الطلبات",
      "shipments": "الشحنات",
      "setup_master": "إعداد الماستر",
      "welcome_back": "مرحباً بعودتك",
      "login_subtitle": "أدخل بيانات الاعتماد الخاصة بك للمتابعة.",
      "search_placeholder": "بحث عن أي شيء...",
      "account_profile": "ملف الحساب",
      "color_theme": "سمة اللون",
      "system_settings": "إعدادات النظام",
      "logout": "تسجيل الخروج",
      "create_new": "إنشاء جديد",
      "sync": "مزامنة",
      "live": "مباشر",
      "language": "اللغة"
    }
  },
  ur: {
    translation: {
      "dashboard": "ڈیش بورڈ",
      "procurement": "خریداری",
      "quotations": "کوٹیشنز",
      "orders": "آرڈرز",
      "shipments": "شحنات",
      "setup_master": "سیٹ اپ ماسٹر",
      "welcome_back": "خوش آمدید",
      "login_subtitle": "جاری رکھنے کے لیے اپنی آپریشنل اسناد درج کریں۔",
      "search_placeholder": "کچھ بھی تلاش کریں...",
      "account_profile": "اکاؤنٹ پروفائل",
      "color_theme": "رنگین تھیم",
      "system_settings": "سسٹم کی ترتیبات",
      "logout": "لاگ آؤٹ سیشن",
      "create_new": "نیا بنائیں",
      "sync": "سنکرونائز",
      "live": "لائیو",
      "language": "زبان"
    }
  },
  es: {
    translation: {
      "dashboard": "Tablero",
      "procurement": "Adquisiciones",
      "quotations": "Cotizaciones",
      "orders": "Pedidos",
      "shipments": "Envíos",
      "setup_master": "Maestro de Configuración",
      "welcome_back": "Bienvenido de nuevo",
      "login_subtitle": "Ingrese sus credenciales operativas para continuar.",
      "search_placeholder": "Buscar algo...",
      "account_profile": "Perfil de cuenta",
      "color_theme": "Tema de color",
      "system_settings": "Configuración del sistema",
      "logout": "Cerrar sesión",
      "create_new": "Crear nuevo",
      "sync": "Sincronizar",
      "live": "En vivo",
      "language": "Idioma"
    }
  },
  fr: {
    translation: {
      "dashboard": "Tableau de bord",
      "procurement": "Achats",
      "quotations": "Devis",
      "orders": "Commandes",
      "shipments": "Expéditions",
      "setup_master": "Maître de configuration",
      "welcome_back": "Bon retour",
      "login_subtitle": "Entrez vos identifiants opérationnels pour continuer.",
      "search_placeholder": "Rechercher...",
      "account_profile": "Profil du compte",
      "color_theme": "Thème de couleur",
      "system_settings": "Paramètres système",
      "logout": "Déconnexion",
      "create_new": "Créer nouveau",
      "sync": "Synchroniser",
      "live": "En direct",
      "language": "Langue"
    }
  },
  de: {
    translation: {
      "dashboard": "Dashboard",
      "procurement": "Beschaffung",
      "quotations": "Angebote",
      "orders": "Bestellungen",
      "shipments": "Lieferungen",
      "setup_master": "Konfigurations-Master",
      "welcome_back": "Willkommen zurück",
      "login_subtitle": "Geben Sie Ihre Anmeldedaten ein, um fortzufahren.",
      "search_placeholder": "Suche...",
      "account_profile": "Kontoprofil",
      "color_theme": "Farbschema",
      "system_settings": "Systemeinstellungen",
      "logout": "Abmelden",
      "create_new": "Neu erstellen",
      "sync": "Synchronisieren",
      "live": "Live",
      "language": "Sprache"
    }
  }
};

i18n
  .use(LanguageDetector)
  .use(initReactI18next)
  .init({
    resources,
    fallbackLng: 'en',
    interpolation: {
      escapeValue: false,
    },
  });

export default i18n;
