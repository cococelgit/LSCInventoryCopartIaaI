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
  gallery: string[];
  publicFacts?: { color?: string; fuel?: string; transmission?: string; drive?: string; engine?: string; titleType?: string; damage?: string; location?: string };
};

export const vehicles: Vehicle[] = [
  { lot: "48841576", title: "2012 Chevrolet Malibu 2LT", year: 2012, make: "Chevrolet", model: "Malibu 2LT", currentBid: null, photos: 1, auctionDate: "31 AGO 2026", lotStatus: "Sin puja actual", availability: "Revisión requerida", gallery: ["/manus-storage/lot-48841576_921f8813.jpg", "/manus-storage/lot-48841576-2_32cbac91.jpg", "/manus-storage/lot-48841576-3_12126063.jpg", "/manus-storage/lot-48841576-4_bbcb8e1f.jpg"] },
  { lot: "50566696", title: "2004 Workhorse P42 Delivery Truck", year: 2004, make: "Workhorse", model: "P42 Delivery Truck", currentBid: 450, photos: 13, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado", gallery: ["/manus-storage/lot-50566696_5cb42580.jpg", "/manus-storage/lot-50566696-2_1dbe98a6.jpg", "/manus-storage/lot-50566696-3_49fc06a4.jpg", "/manus-storage/lot-50566696-4_895471e1.jpg"] },
  { lot: "52483876", title: "2019 Ford Fiesta SE", year: 2019, make: "Ford", model: "Fiesta SE", currentBid: 450, photos: 1, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado", gallery: ["/manus-storage/lot-52483876_28573cb8.jpg", "/manus-storage/lot-52483876-2_504823bf.jpg", "/manus-storage/lot-52483876-3_4c32879a.jpg", "/manus-storage/lot-52483876-4_0dbd7aab.jpg"] },
  { lot: "53629776", title: "2025 Chevrolet Silverado C1500 RST", year: 2025, make: "Chevrolet", model: "Silverado C1500 RST", currentBid: 14600, photos: 12, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado", gallery: ["/manus-storage/lot-53629776_52a1f9eb.jpg", "/manus-storage/lot-53629776-2_9887fdc0.jpg", "/manus-storage/lot-53629776-3_bd3c4053.jpg", "/manus-storage/lot-53629776-4_4867740d.jpg"], publicFacts: { color: "Blanco", fuel: "Diésel", transmission: "Automática", drive: "Tracción trasera", engine: "3.0L 6", titleType: "Certificate of Destruction", damage: "Agua / inundación", location: "FL · Clewiston" } },
  { lot: "54292856", title: "2025 Dodge Charger Daytona R", year: 2025, make: "Dodge", model: "Charger Daytona R", currentBid: 5900, photos: 12, auctionDate: "31 AGO 2026", lotStatus: "En seguimiento", availability: "Verificado", gallery: ["/manus-storage/lot-54292856_0f2581d6.jpg", "/manus-storage/lot-54292856-2_810474b1.jpg", "/manus-storage/lot-54292856-3_e855eb71.jpg", "/manus-storage/lot-54292856-4_ff3e109d.jpg"] },
];

export function formatMoney(value: number | null) {
  return value === null ? "No reportada" : new Intl.NumberFormat("en-US", { style: "currency", currency: "USD", maximumFractionDigits: 0 }).format(value);
}

export function vehicleFromLot(lot: string) {
  return vehicles.find((vehicle) => vehicle.lot === lot);
}
