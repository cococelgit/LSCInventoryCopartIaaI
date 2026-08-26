/** Style reminder — Azul, rojo y blanco de inspiración cubana aplicados a una consola de inventario limpia y auditable. */
import { Toaster } from "@/components/ui/sonner";
import { TooltipProvider } from "@/components/ui/tooltip";
import { Route, Switch } from "wouter";
import ErrorBoundary from "./components/ErrorBoundary";
import { ThemeProvider } from "./contexts/ThemeContext";
import Home from "./pages/Home";
import InternalDiscards from "./pages/InternalDiscards";
import NotFound from "./pages/NotFound";
import VehicleDetail from "./pages/VehicleDetail";

function Router() { return <Switch><Route path="/" component={Home} /><Route path="/vehiculo/:lot" component={VehicleDetail} /><Route path="/interno/descartes" component={InternalDiscards} /><Route path="/404" component={NotFound} /><Route component={NotFound} /></Switch>; }
export default function App() { return <ErrorBoundary><ThemeProvider defaultTheme="light"><TooltipProvider><Toaster /><Router /></TooltipProvider></ThemeProvider></ErrorBoundary>; }
