// One separable Gaussian pass along Direction
Texture2D<float4> Source : register(t0);
RWTexture2D<float4> Output : register(u0);

cbuffer Constants : register(b0)
{
	int2 Direction;
	uint2 Size;
	float Sigma;
	int Radius;
	float2 Padding;
};

[numthreads(8, 8, 1)]
void CS(uint2 id : SV_DispatchThreadID)
{
	if (any(id >= Size))
		return;
	float4 sum = 0;
	float weights = 0;
	for (int i = -Radius; i <= Radius; i++)
	{
		int2 p = clamp(int2(id) + Direction * i, 0, int2(Size) - 1);
		float w = exp(-(i * i) / (2.0f * Sigma * Sigma));
		sum += Source[p] * w;
		weights += w;
	}
	Output[id] = sum / weights;
}
