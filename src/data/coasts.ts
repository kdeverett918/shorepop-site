export type Coast = {
  number: number
  name: string
  light: string
  slug: string
  /** Sky colour used behind the panel, sampled from the coast's own light. */
  sky: string
  ink: string
  map: string
  mapAlt: string
}

export const coasts: Coast[] = [
  {
    number: 1,
    name: 'Arrival Cove',
    light: 'a sunlit cove, mid-morning',
    slug: 'coast-1-arrival-cove',
    sky: '#a9e2f5',
    ink: '#0b2b53',
    map: '/img/map-world-1.webp',
    mapAlt:
      'Arrival Cove map screen: the fox stands beside level 7 on a white road over green and sand terraces, with Rocket and Bomb boosters and a Play button below.',
  },
  {
    number: 2,
    name: 'Glasshouse Garden',
    light: 'late morning under glass',
    slug: 'coast-2-glasshouse-garden',
    sky: '#bfe8f2',
    ink: '#0b2b53',
    map: '/img/map-world-2.webp',
    mapAlt:
      'Glasshouse Garden map screen: the road winds past blue glass domes to level 19, Seedling Steps.',
  },
  {
    number: 3,
    name: 'Sunset Market',
    light: 'golden hour, sun low in the west',
    slug: 'coast-3-sunset-market',
    sky: '#f6c48f',
    ink: '#0b2b53',
    map: '/img/map-world-3.webp',
    mapAlt:
      'Sunset Market map screen: terracotta market buildings in golden-hour light with the fox on the road.',
  },
  {
    number: 4,
    name: 'Cloudfall Isles',
    light: 'islands above the clouds, just past dawn',
    slug: 'coast-4-cloudfall-isles',
    sky: '#cfe7f4',
    ink: '#0b2b53',
    map: '/img/map-world-4.webp',
    mapAlt:
      'Cloudfall Isles map screen: floating islands and clouds with the road climbing to level 43, Floating Lanterns.',
  },
  {
    number: 5,
    name: 'Lantern Harbor',
    light: 'night, past the blue hour',
    slug: 'coast-5-lantern-harbor',
    sky: '#2a3a72',
    ink: '#fff6e6',
    map: '/img/map-world-5.webp',
    mapAlt:
      'Lantern Harbor map screen: teal harbor towers at night lit by lanterns, with the fox on the road.',
  },
]
