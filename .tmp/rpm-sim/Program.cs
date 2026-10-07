using System;
class P {
  static void Run(string label, float throttle, bool ease, float accelBase) {
    float speedMax=314.5f, rpmMin=600, rpmMax=9500;
    float gearPullScale=1.2f, gearCap=54.89f;
    float gearPull = 1f + (1.40f - 1f) * gearPullScale; // 1.48
    float accel = accelBase * gearPull;
    float speedScale = speedMax / 350f;
    float speed=0, rpm=rpmMin, dt=1f/60f, maxGsf=0, maxRpm=0, phase=0;
    float bounceAmt=0.101f, bounceHz=12f;
    for (int i=0;i<60*20;i++) {
      float gap=Math.Max(0,gearCap-speed);
      float band=Math.Max(6f,gearCap*0.14f);
      float linear=gap<=0?0:Math.Clamp(gap/band,0,1);
      float hr=linear<=0?0:MathF.Sqrt(linear);
      if (gap>0) speed += Math.Min(gap, throttle*accel*speedScale*hr*dt);
      float lift=1-throttle;
      float aero=45f*1f*Math.Clamp(speed/gearCap,0,1);
      aero=aero*aero/(45f); // wrong
      float an=Math.Clamp(speed/gearCap,0,1);
      aero=45f*an*an*lift*lift;
      speed -= aero*speedScale*dt;
      if (ease && throttle>=0.75f && speed>gearCap*0.82f) {
        float remain=gearCap-speed;
        if (remain>0) speed += remain*(1f-MathF.Exp(-dt/0.55f))*throttle;
      }
      speed=Math.Min(speed,gearCap);
      float gsf=Math.Clamp(speed/gearCap,0,1);
      float gearRatio=0.55f;
      float loadPull=throttle*(0.12f+0.10f*(1f-gearRatio))*(1f-gsf);
      float rpmFrac=Math.Clamp(gsf+loadPull,0,1);
      float tgt=rpmMin+(rpmMax-rpmMin)*rpmFrac;
      float climbSec=0.07f+0.18f*Math.Clamp(2f-gearPull,0,1.6f);
      rpm+=(tgt-rpm)*(1f-MathF.Exp(-dt/climbSec));
      float gearPin=Math.Clamp((gsf-0.82f)/0.18f,0,1)*Math.Clamp((throttle-0.30f)/0.45f,0,1);
      phase+=dt*(float)(Math.PI*2*bounceHz);
      float wave=MathF.Pow(MathF.Abs(MathF.Sin(phase)),0.55f);
      float depth=(rpmMax-rpmMin)*0.10f*bounceAmt*gearPin;
      float rpmOut=Math.Clamp(rpm-depth*wave,rpmMin,rpmMax);
      maxGsf=Math.Max(maxGsf,gsf);
      maxRpm=Math.Max(maxRpm,rpmOut);
      if (i%60==0) Console.WriteLine($"  t={i/60f:0} gsf={gsf:0.000} rpm={rpmOut:0} tgt={tgt:0}");
    }
    Console.WriteLine($"{label}: maxGsf={maxGsf:0.0000} maxRpmSeen={maxRpm:0} (configured max {rpmMax})\n");
  }
  static void Main() {
    Console.WriteLine("User settings: Accel=24.3 Gear1=54.9 MaxRpm=9500 Redline=9000");
    Run("th1.0 ease", 1.0f, true, 24.3f);
    Run("th0.95 ease", 0.95f, true, 24.3f);
    Run("th0.70 NO ease thresh", 0.70f, true, 24.3f);
    Run("th0.70 no ease code", 0.70f, false, 24.3f);
    Run("th0.90 ease", 0.90f, true, 24.3f);
  }
}
