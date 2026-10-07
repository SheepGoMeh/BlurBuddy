// Capture outside the rectangles, the selected effect inside, feathered edge
Texture2D<float4> Capture : register(t0);
Texture2D<float4> Blurred : register(t1);
StructuredBuffer<float4> Rects : register(t2); // left, top, right, bottom in pixels
RWTexture2D<float4> Output : register(u0);
SamplerState Linear : register(s0);

cbuffer Constants : register(b0)
{
	uint2 Size;
	uint RectCount;
	uint WholeFrame;
	float Feather;
	uint Style; // 0 gaussian, 1 pixelate, 2 crystallize, 3 diamond glass
	float BlockSize;
	float Strength;
};

float3 Pixelate(float2 p)
{
	return Capture[min(uint2((floor(p / BlockSize) + 0.5f) * BlockSize), Size - 1)].rgb; // block centre
}

float2 Hash(float2 cell)
{
	float3 q = frac(float3(cell.xyx) * float3(0.1031f, 0.1030f, 0.0973f));
	q += dot(q, q.yzx + 33.33f);
	return frac((q.xx + q.yz) * q.zy);
}

float3 Crystallize(float2 p)
{
	// Voronoi on a jittered grid: each pixel takes the colour at its nearest seed (paint.net Crystallize)
	float2 cell = floor(p / BlockSize);
	float2 nearest = p;
	float best = 1e30f;
	for (int y = -1; y <= 1; y++)
	{
		for (int x = -1; x <= 1; x++)
		{
			float2 neighbour = cell + float2(x, y);
			float2 seed = (neighbour + Hash(neighbour)) * BlockSize;
			float2 d = seed - p;
			float distance = dot(d, d);
			if (distance < best)
			{
				best = distance;
				nearest = seed;
			}
		}
	}
	return Capture[min(uint2(max(nearest, 0)), Size - 1)].rgb;
}

static const float Sqrt2 = 1.41421356f;

float3 Diamond(float2 p)
{
	// 45 degree grid: each diamond takes the blurred colour at its centre, darker rims for a glass look
	float2 q = float2(p.x + p.y, p.y - p.x) / Sqrt2;
	float2 cell = (floor(q / BlockSize) + 0.5f) * BlockSize;
	float2 centre = float2(cell.x - cell.y, cell.x + cell.y) / Sqrt2;
	float3 colour = Blurred.SampleLevel(Linear, centre / Size, 0).rgb;
	float2 f = frac(q / BlockSize);
	float rim = min(min(f.x, 1 - f.x), min(f.y, 1 - f.y));
	float facet = 0.04f * (f.x - f.y); // light from the top left
	return colour * lerp(0.82f, 1.06f, saturate(rim * 6)) + facet;
}

[numthreads(8, 8, 1)]
void CS(uint2 id : SV_DispatchThreadID)
{
	if (any(id >= Size))
		return;
	float2 p = id + 0.5f;
	float mask = WholeFrame;
	// ponytail: every pixel tests every rectangle; tile binning if this shows up in GPU time
	for (uint i = 0; i < RectCount && mask < 1; i++)
	{
		float4 r = Rects[i];
		float2 d = max(r.xy - p, p - r.zw);
		mask = max(mask, saturate(1 - max(d.x, d.y) / Feather));
	}

	float4 colour = Capture[id];
	if (mask <= 0)
	{
		Output[id] = float4(colour.rgb, 1);
		return;
	}

	float3 hidden;
	switch (Style)
	{
		case 1: hidden = Pixelate(p); break;
		case 2: hidden = Crystallize(p); break;
		case 3: hidden = Diamond(p); break;
		default: hidden = Blurred.SampleLevel(Linear, p / Size, 0).rgb; break;
	}
	Output[id] = float4(lerp(colour.rgb, hidden, mask), 1);
}
