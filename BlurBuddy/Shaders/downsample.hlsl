// Half resolution copy, bilinear averages each 2x2 block
Texture2D<float4> Source : register(t0);
RWTexture2D<float4> Output : register(u0);
SamplerState Linear : register(s0);

cbuffer Constants : register(b0)
{
	float2 InvOutputSize;
	uint2 OutputSize;
};

[numthreads(8, 8, 1)]
void CS(uint2 id : SV_DispatchThreadID)
{
	if (any(id >= OutputSize))
		return;
	Output[id] = Source.SampleLevel(Linear, (id + 0.5f) * InvOutputSize, 0);
}
