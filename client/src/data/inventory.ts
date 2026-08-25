/** Style reminder — Datos seguros de un explorador limpio: solo campos observados, ausencia explícita y cero inferencias. */
export type Vehicle = {
  lot: string;
  title: string;
  year: number;
  make: string;
  model: string;
  currentBid: number | null;
  photos: number;
  auctionDate: string;
  lotStatus: "En seguimiento" | "Sin puja actual";
  availability: "Verificado" | "Revisión requerida";
};

export const vehicles: Vehicle[] = [
  { lot: "48841576", title: "2012 Chevrolet Malibu 2LT", year: 2012, make: "Chevrolet", model: "Malibu 2LT", currentBid: null, photos: 1, auctionDate: "31 AGO 2026", lotStatus: "Sin puja actual", availability: "Revisión requerida" },
  { lot: "50566696", title: "2004 Workhorse P42 Delivery Truck", year: 2004, make: "Workhorse", model: "P42 Delivery Truck", currentBid: 450, photos: 13, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado" },
  { lot: "52483876", title: "2019 Ford Fiesta SE", year: 2019, make: "Ford", model: "Fiesta SE", currentBid: 450, photos: 1, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado" },
  { lot: "53629776", title: "2025 Chevrolet Silverado C1500 RST", year: 2025, make: "Chevrolet", model: "Silverado C1500 RST", currentBid: 14600, photos: 12, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado" },
  { lot: "54292856", title: "2025 Dodge Charger Daytona R", year: 2025, make: "Dodge", model: "Charger Daytona R", currentBid: 5900, photos: 12, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado" },
];

export function formatMoney(value: number | null) {
  return value === null ? "No reportada" : new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 }).format(value);
}

export function vehicleFromLot(lot: string) {
  return vehicles.find((vehicle) => vehicle.lot === lot);
}
