import React, { useMemo, useRef, useState } from "react";
import { ChevronLeft, ChevronRight, Image } from "lucide-react";

type VehiclePhotoCarouselProps = {
  photos: string[];
  title: string;
  lot: string;
  href: string;
};

const SWIPE_THRESHOLD_PX = 42;

export default function VehiclePhotoCarousel({ photos, title, lot, href }: VehiclePhotoCarouselProps) {
  const [failedPhotos, setFailedPhotos] = useState<Set<string>>(() => new Set());
  const safePhotos = useMemo(() => photos.filter((candidate) => candidate && !failedPhotos.has(candidate)), [photos, failedPhotos]);
  const [activePhoto, setActivePhoto] = useState(0);
  const touchStartX = useRef<number | null>(null);
  const swipeGuardUntil = useRef(0);
  const photo = safePhotos[activePhoto];
  const hasMultiple = safePhotos.length > 1;

  const move = (direction: -1 | 1) => {
    if (!hasMultiple) return;
    setActivePhoto((current) => (current + direction + safePhotos.length) % safePhotos.length);
  };

  const handleTouchStart = (event: React.TouchEvent<HTMLDivElement>) => {
    touchStartX.current = event.touches[0]?.clientX ?? null;
  };

  const handleTouchEnd = (event: React.TouchEvent<HTMLDivElement>) => {
    if (touchStartX.current === null) return;
    const endX = event.changedTouches[0]?.clientX;
    const distance = typeof endX === "number" ? endX - touchStartX.current : 0;
    touchStartX.current = null;
    if (Math.abs(distance) < SWIPE_THRESHOLD_PX) return;
    swipeGuardUntil.current = Date.now() + 500;
    move(distance > 0 ? -1 : 1);
  };

  return <div className="browse-photo" onTouchStart={handleTouchStart} onTouchEnd={handleTouchEnd}>
    <a
      href={href}
      target="_blank"
      rel="noreferrer"
      className="browse-photo-link"
      aria-label={`Abrir ficha de ${title} en una nueva pestaña`}
      onClick={(event) => {
        if (Date.now() < swipeGuardUntil.current) event.preventDefault();
      }}
    >
      {photo
        ? <img
          src={photo}
          alt={`${title}, foto ${activePhoto + 1} de ${safePhotos.length}, lote ${lot}`}
          loading="lazy"
          decoding="async"
          onError={() => {
            setFailedPhotos((current) => new Set(current).add(photo));
            setActivePhoto(0);
          }}
        />
        : <div className="browse-photo-empty"><Image size={28} /><span>Sin foto</span></div>}
    </a>
    <span className="browse-photo-count" aria-live="polite"><Image size={12} /> {safePhotos.length ? `${activePhoto + 1} / ${safePhotos.length}` : "0 fotos"}</span>
    {hasMultiple && <div className="browse-photo-controls" role="group" aria-label={`Fotos del lote ${lot}`}>
      <button type="button" onClick={() => move(-1)} aria-label={`Foto anterior de ${title}`}><ChevronLeft size={16} /></button>
      <button type="button" onClick={() => move(1)} aria-label={`Foto siguiente de ${title}`}><ChevronRight size={16} /></button>
    </div>}
  </div>;
}
