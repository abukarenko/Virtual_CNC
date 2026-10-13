namespace VirtualCnc.Core;

public sealed partial class GrblController
{
    private enum HomePhase { Seek, SeekDebounce, PullOff, PullOffDebounce, Locate, LocateDebounce, FinalPullOff, FinalDebounce }
    private sealed class HomingCycle
    {
        public int Mask = 4; // Stock GRBL: Z first, then X/Y together.
        public HomePhase Phase;
        public Vec Target, Boundary, Continuous;
        public double Delay;
        public int ContactMask;
    }
    private HomingCycle? homing;
    private string homingLimits = "", travelLimits = "";
    private string EffectiveLimits => string.Concat("XYZ".Where(c => limits.Contains(c) || homingLimits.Contains(c) || travelLimits.Contains(c)));

    private bool UpdateTravelLimits()
    {
        var before=EffectiveLimits;
        var physical=PhysicalPosition;
        travelLimits=string.Concat(Enumerable.Range(0,3).Where(i=>physical[i]>1e-9 || physical[i]<-Setting(130+i)-1e-9).Select(i=>"XYZ"[i]));
        var after=EffectiveLimits;
        if(before!=after) LimitsChanged?.Invoke(after);
        if(travelLimits.Length>0 && Setting(21)!=0 && state is not ("Home" or "Alarm")) {Alarm(1);return true;}
        return false;
    }
    private void BeginHoming()
    {
        position=PhysicalPosition;physicalOffset=default;
        state = "Home"; currentFeed = velocity = actualSpeed = 0;
        actualSpindle = 5; actualCoolant = 0;
        homing = new(); homingLimits = "";
        BeginHomePhase(HomePhase.Seek);
    }
    private void BeginHomePhase(HomePhase phase)
    {
        var cycle = homing!;
        cycle.Phase = phase; cycle.ContactMask = 0; cycle.Target = position;
        cycle.Continuous = position;
        bool approach = phase is HomePhase.Seek or HomePhase.Locate;
        for (int i=0;i<3;i++)
        {
            if ((cycle.Mask & (1<<i)) == 0) continue;
            var negative = ((int)Setting(23) & (1<<i)) != 0;
            var boundary = negative ? -Setting(130+i) : 0;
            cycle.Boundary = cycle.Boundary.With(i, boundary);
            var direction = negative ? -1 : 1;
            var target = approach ? boundary : boundary - direction*Setting(27);
            if (approach && !HomingSwitchContact)
                target = position[i] + direction*(phase == HomePhase.Seek ? 1.5*Setting(130+i) : 5*Setting(27));
            cycle.Target = cycle.Target.With(i,target);
        }
        modal.Position = cycle.Target;
    }
    private void AdvanceHoming(double seconds)
    {
        while (seconds > 0 && homing is { } cycle && state == "Home")
        {
            if (cycle.Phase is HomePhase.SeekDebounce or HomePhase.PullOffDebounce or HomePhase.LocateDebounce or HomePhase.FinalDebounce)
            {
                currentFeed = 0;
                var spent = Math.Min(seconds, cycle.Delay);
                cycle.Delay -= spent; seconds -= spent;
                if (cycle.Delay > 1e-9) return;
                if (cycle.Phase == HomePhase.FinalDebounce)
                {
                    if (cycle.Mask == 4) { cycle.Mask = 3; BeginHomePhase(HomePhase.Seek); }
                    else
                    {
                        homing = null; homingLimits = ""; awaitingInitialHome=false; modal.Position = position;
                        state = "Idle"; statusCounter=0; RunStartup(); CompleteAck(); return;
                    }
                }
                else BeginHomePhase(cycle.Phase switch
                {
                    HomePhase.SeekDebounce => HomePhase.PullOff,
                    HomePhase.PullOffDebounce => HomePhase.Locate,
                    _ => HomePhase.FinalPullOff
                });
                continue;
            }

            bool approach = cycle.Phase is HomePhase.Seek or HomePhase.Locate;
            var rate = Setting(cycle.Phase == HomePhase.Locate ? 24 : 25)/60;
            double remaining=0;
            for(int i=0;i<3;i++)
            {
                if ((cycle.Mask & (1<<i)) == 0) continue;
                if (approach && (limits.Contains("XYZ"[i]) || (HomingSwitchContact && Math.Abs(cycle.Continuous[i]-cycle.Boundary[i]) < 1e-9)))
                    cycle.ContactMask |= 1<<i;
                if ((cycle.ContactMask & (1<<i)) == 0) remaining=Math.Max(remaining, Math.Abs(cycle.Target[i]-cycle.Continuous[i]));
            }
            if (approach) SetHomingLimits(cycle.ContactMask, cycle.Mask);
            if (remaining > 1e-9 && rate <= 0) { currentFeed=0; return; }
            var stepTime=remaining == 0 ? 0 : Math.Min(seconds,Math.Min(.01,remaining/rate));
            var previousPosition=cycle.Continuous;
            var continuous=cycle.Continuous;
            for(int i=0;i<3;i++)
            {
                if ((cycle.Mask & (1<<i)) == 0 || (cycle.ContactMask & (1<<i)) != 0) continue;
                var delta=cycle.Target[i]-continuous[i];
                var distance=Math.Min(Math.Abs(delta),rate*stepTime);
                continuous=continuous.With(i,continuous[i]+Math.Sign(delta)*distance);
                if (Math.Abs(cycle.Target[i]-continuous[i]) < 1e-9) continuous=continuous.With(i,cycle.Target[i]);
                if (approach && HomingSwitchContact && Math.Abs(continuous[i]-cycle.Boundary[i]) < 1e-9) cycle.ContactMask|=1<<i;
            }
            // Keep sub-step progress internally so low homing feed cannot stall on rounding.
            cycle.Continuous=continuous;
            position=Quantize(continuous); UpdateTravelLimits();
            if (continuous != previousPosition) PositionChanged?.Invoke(position);
            seconds-=stepTime;
            currentFeed=stepTime>0 ? (continuous-previousPosition).Length/stepTime*60 : 0;
            if (approach) SetHomingLimits(cycle.ContactMask,cycle.Mask);
            else
            {
                int mask=0;
                for(int i=0;i<3;i++) if ((cycle.Mask&(1<<i))!=0 && Math.Abs(continuous[i]-cycle.Boundary[i])<1e-9) mask|=1<<i;
                SetHomingLimits(mask,cycle.Mask);
            }
            bool done=approach ? cycle.ContactMask==cycle.Mask : Enumerable.Range(0,3).All(i=>(cycle.Mask&(1<<i))==0 || Math.Abs(continuous[i]-cycle.Target[i])<1e-9);
            if (!done)
            {
                if (approach && Enumerable.Range(0,3).All(i=>(cycle.Mask&(1<<i))==0 || (cycle.ContactMask&(1<<i))!=0 || Math.Abs(continuous[i]-cycle.Target[i])<1e-9)) { Alarm(9); return; }
                if (seconds <= 1e-9) return;
                continue;
            }
            if (!approach && Enumerable.Range(0,3).Any(i=>(cycle.Mask&(1<<i))!=0 && EffectiveLimits.Contains("XYZ"[i]))) { Alarm(8); return; }
            position=Quantize(position); modal.Position=position; currentFeed=0;
            cycle.Phase = cycle.Phase switch
            {
                HomePhase.Seek => HomePhase.SeekDebounce,
                HomePhase.PullOff => HomePhase.PullOffDebounce,
                HomePhase.Locate => HomePhase.LocateDebounce,
                _ => HomePhase.FinalDebounce
            };
            cycle.Delay=Setting(26)/1000;
        }
    }
    private void SetHomingLimits(int contacts,int activeAxes)
    {
        var before=EffectiveLimits;
        homingLimits=string.Concat(Enumerable.Range(0,3).Where(i=>(contacts&(1<<i))!=0 || ((activeAxes&(1<<i))==0 && homingLimits.Contains("XYZ"[i]))).Select(i=>"XYZ"[i]));
        if (before!=EffectiveLimits) LimitsChanged?.Invoke(EffectiveLimits);
    }
}
